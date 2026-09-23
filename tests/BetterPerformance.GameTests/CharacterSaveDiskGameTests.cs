using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

// Verifies the CharacterSaveToDisk phase probes against the installed assemblies and
// exercises the private hook methods directly with a synthetic session. The targets span
// assembly_valheim (PlayerProfile, SaveSystem, ZPackage, ZNet), assembly_utils (FileWriter,
// FileHelpers) and Splatform.dll (CloudStorageFileGrouping); none of the last two are
// referenced by the plugin project at compile time, matching CloudWriteOptimization's own
// resolution. Never mounts cloud storage, hashes anything, writes a file, or calls the real
// character-save path.
internal static class CharacterSaveDiskGameTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Run(Assembly game, Assembly plugin, string managedDirectory)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("CharacterSaveDisk: " + message);
            checks++;
        }

        Assembly utils = Assembly.LoadFrom(Path.Combine(managedDirectory, "assembly_utils.dll"));
        Assembly splatform = Assembly.LoadFrom(Path.Combine(managedDirectory, "Splatform.dll"));
        Type playerProfile = game.GetType("PlayerProfile", true)!;
        Type saveSystem = game.GetType("SaveSystem", true)!;
        Type package = game.GetType("ZPackage", true)!;
        Type znet = game.GetType("ZNet", true)!;
        Type saveDataType = game.GetType("SaveDataType", true)!;
        Type world = game.GetType("World", true)!;
        Type fileHelpers = utils.GetType("FileHelpers", true)!;
        Type fileSource = utils.GetType("FileHelpers+FileSource", true)!;
        Type fileHelperType = utils.GetType("FileHelpers+FileHelperType", true)!;
        Type fileWriter = utils.GetType("FileWriter", true)!;
        Type grouping = splatform.GetType("Splatform.CloudStorageFileGrouping", true)!;

        MethodInfo saveToDisk = playerProfile.GetMethod("SavePlayerToDisk", Instance, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: PlayerProfile.SavePlayerToDisk is missing.");
        Check(!saveToDisk.IsStatic && saveToDisk.ReturnType == typeof(bool),
            "PlayerProfile.SavePlayerToDisk keeps its private instance bool signature");

        MethodInfo cloudChecks = saveSystem.GetMethod("PreSaveCloudChecksAndOperations", Static, null,
            new[] { typeof(string), saveDataType, fileSource.MakeByRefType(), typeof(bool).MakeByRefType(),
                typeof(bool).MakeByRefType(), typeof(int), world }, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: SaveSystem.PreSaveCloudChecksAndOperations is missing.");
        Check(cloudChecks.IsStatic && cloudChecks.ReturnType == typeof(void),
            "SaveSystem.PreSaveCloudChecksAndOperations keeps its exact ref-parameter shape");

        MethodInfo generateHash = package.GetMethod("GenerateHash", Instance, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: ZPackage.GenerateHash is missing.");
        MethodInfo getArray = package.GetMethod("GetArray", Instance, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: ZPackage.GetArray is missing.");
        Check(!generateHash.IsStatic && generateHash.ReturnType == typeof(byte[]), "ZPackage.GenerateHash returns byte[]");
        Check(!getArray.IsStatic && getArray.ReturnType == typeof(byte[]), "ZPackage.GetArray returns byte[]");
        Check(CallsDirectly(generateHash, getArray), "GenerateHash still hashes GetArray's own bytes, not a separate copy");

        ConstructorInfo writerCtor = fileWriter.GetConstructor(Instance, null,
            new[] { typeof(string), grouping, fileHelperType, fileSource }, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: FileWriter constructor is missing.");
        MethodInfo finish = fileWriter.GetMethod("Finish", Instance, null, Type.EmptyTypes, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: FileWriter.Finish is missing.");
        Check(!finish.IsStatic && finish.ReturnType == typeof(void), "FileWriter.Finish keeps its void instance shape");

        MethodInfo replace = fileHelpers.GetMethod("ReplaceOldFile", Static, null,
            new[] { typeof(string), typeof(string), typeof(string), grouping, fileSource }, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: FileHelpers.ReplaceOldFile is missing.");
        Check(replace.IsStatic && replace.ReturnType == typeof(void), "FileHelpers.ReplaceOldFile keeps its exact shape");

        MethodInfo autoBackup = znet.GetMethod("ConsiderAutoBackup", Static, null,
            new[] { typeof(string), saveDataType, typeof(DateTime) }, null)
            ?? throw new InvalidOperationException("CharacterSaveDisk: ZNet.ConsiderAutoBackup is missing.");
        Check(autoBackup.IsStatic && autoBackup.ReturnType == typeof(bool), "ZNet.ConsiderAutoBackup keeps its exact shape");

        Type metricType = plugin.GetType("BetterPerformance.Core.Metric", true)!;
        foreach (string name in new[] { "CharacterSaveCloudChecks", "CharacterSaveHash", "CharacterSaveWrite", "CharacterSaveReplace", "CharacterSaveBackup" })
            Check(Enum.IsDefined(metricType, name), name + ": metric is exportable");

        Type telemetry = plugin.GetType("BetterPerformance.CharacterSaveDiskTelemetry", true)!;
        MethodInfo install = telemetry.GetMethod("Install", Static)!;
        MethodInfo uninstall = telemetry.GetMethod("Uninstall", Static)!;
        PropertyInfo installedProperty = telemetry.GetProperty("Installed", Static)!;
        PropertyInfo statusProperty = telemetry.GetProperty("Status", Static)!;
        Harmony patches = (Harmony)telemetry.GetField("Patches", Static)!.GetValue(null)!;
        string pluginId = (string)plugin.GetType("BetterPerformance.Plugin", true)!.GetField("PluginId")!.GetRawConstantValue()!;

        ConfigFile NewConfig() => new ConfigFile(
            Path.Combine(Path.GetTempPath(), "bp-character-save-disk-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };

        // Config off: no patch anywhere, no half-installed state.
        var disabledConfig = NewConfig();
        disabledConfig.Bind("Diagnostics", "CharacterSaveDiskPhases", false);
        install.Invoke(null, new object[] { disabledConfig, new ManualLogSource("CharacterSaveDiskVerification") });
        Check(!(bool)installedProperty.GetValue(null)!, "disabled configuration installs nothing");
        Check((string)statusProperty.GetValue(null)! == "disabled", "disabled configuration reports disabled");
        foreach (MethodBase method in new MethodBase[] { saveToDisk, cloudChecks, generateHash, getArray, writerCtor, finish, replace, autoBackup })
            Check(Harmony.GetPatchInfo(method)?.Owners.Contains(patches.Id) != true, method.Name + ": disabled configuration leaves it unpatched");

        // Default configuration installs every phase probe with the exact expected shape.
        install.Invoke(null, new object[] { NewConfig(), new ManualLogSource("CharacterSaveDiskVerification") });
        try
        {
            Check((bool)installedProperty.GetValue(null)!, "default configuration installs the phase probes");
            Check((string)statusProperty.GetValue(null)! == "installed", "default configuration reports installed");

            void CheckPatch(MethodBase method, bool hasPrefix, bool hasFinalizer, bool hasPostfix, string label)
            {
                var info = Harmony.GetPatchInfo(method);
                Check(info != null, label + ": patch info exists");
                // Other modules of this harness may own patches here too; ours must be present.
                Check(info!.Owners.Contains(patches.Id), label + ": this module's harmony id owns a patch");
                Check(info.Prefixes.Any(p => p.owner == patches.Id) == hasPrefix, label + ": expected prefix presence");
                Check(info.Finalizers.Any(p => p.owner == patches.Id) == hasFinalizer, label + ": expected finalizer presence");
                Check(info.Postfixes.Any(p => p.owner == patches.Id) == hasPostfix, label + ": expected postfix presence");
                Check(!info.Transpilers.Any(p => p.owner == patches.Id), label + ": observational patch only, no transpiler");
            }
            CheckPatch(saveToDisk, true, true, false, "SavePlayerToDisk");
            CheckPatch(cloudChecks, true, true, false, "PreSaveCloudChecksAndOperations");
            CheckPatch(generateHash, true, true, false, "GenerateHash");
            // MapCompressionCache refuses any owner on GetArray, so this module must never patch it.
            Check(Harmony.GetPatchInfo(getArray)?.Owners.Contains(patches.Id) != true, "GetArray: left unpatched for MapCompressionCache");
            CheckPatch(writerCtor, false, false, true, "FileWriter constructor");
            CheckPatch(finish, true, true, false, "Finish");
            CheckPatch(replace, true, true, false, "ReplaceOldFile");
            CheckPatch(autoBackup, true, true, false, "ConsiderAutoBackup");
        }
        finally { uninstall.Invoke(null, null); }
        Check(Harmony.GetPatchInfo(saveToDisk)?.Owners.Contains(patches.Id) != true, "uninstall removes the SavePlayerToDisk scope patch");
        string cleared = (string)statusProperty.GetValue(null)!;
        Check(cleared == "disabled" || cleared.StartsWith("unpatch_failed_", StringComparison.Ordinal), "uninstall reports its outcome (actual=" + cleared + ")");

        // --- Direct hook-method scenario checks: no game call, no file, no hash. ---
        FieldInfo activeField = telemetry.GetField("active", Static)!;
        FieldInfo writeStartValidField = telemetry.GetField("writeStartValid", Static)!;
        FieldInfo packageBytesField = telemetry.GetField("packageBytes", Static)!;
        FieldInfo probeFailuresField = telemetry.GetField("probeFailures", Static)!;
        FieldInfo fileSourceField = telemetry.GetField("fileSource", Static)!;
        MethodInfo beforeSave = telemetry.GetMethod("BeforeSave", Static)!;
        MethodInfo afterSave = telemetry.GetMethod("AfterSave", Static)!;
        MethodInfo beforePhase = telemetry.GetMethod("BeforePhase", Static)!;
        MethodInfo afterCloudChecks = telemetry.GetMethod("AfterCloudChecks", Static)!;
        MethodInfo afterHash = telemetry.GetMethod("AfterHash", Static)!;
        MethodInfo afterReplace = telemetry.GetMethod("AfterReplace", Static)!;
        MethodInfo afterBackup = telemetry.GetMethod("AfterBackup", Static)!;
        MethodInfo beforeWrite = telemetry.GetMethod("BeforeWrite", Static)!;
        MethodInfo afterWrite = telemetry.GetMethod("AfterWrite", Static)!;
        MethodInfo afterWriterConstructed = telemetry.GetMethod("AfterWriterConstructed", Static)!;
        MethodInfo sample = telemetry.GetMethod("Sample", Static)!;
        MethodInfo reset = telemetry.GetMethod("Reset", Static)!;

        Type timingHooks = plugin.GetType("BetterPerformance.TimingHooks", true)!;
        FieldInfo currentSession = timingHooks.GetField("Current", Static)!;
        object? savedSession = currentSession.GetValue(null);
        object? savedActive = activeField.GetValue(null);
        // The scenario runs after Uninstall; the hooks only act while the module reports installed.
        MethodInfo setInstalled = AccessTools.DeclaredPropertySetter(telemetry, "Installed")!;
        object savedInstalled = installedProperty.GetValue(null)!;
        setInstalled.Invoke(null, new object[] { true });
        try
        {
            object session = FormatterServices.GetUninitializedObject(currentSession.FieldType);
            FieldInfo bookField = currentSession.FieldType.GetField("Book", Instance)!;
            object book = Activator.CreateInstance(bookField.FieldType)!;
            bookField.SetValue(session, book);
            MethodInfo drain = book.GetType().GetMethod("Drain")!;

            object Entry(Array summaries, string name) => summaries.Cast<object>()
                .Single(s => (string)s.GetType().GetProperty("Name")!.GetValue(s)! == name);
            long Count(object entry) => (long)entry.GetType().GetProperty("Count")!.GetValue(entry)!;
            long Failed(object entry) => (long)entry.GetType().GetProperty("FailedCalls")!.GetValue(entry)!;

            reset.Invoke(null, null);
            activeField.SetValue(null, false);
            currentSession.SetValue(null, session);

            // Outside the SavePlayerToDisk scope, a foreign GenerateHash/GetArray/Finish
            // (a concurrent world save uses the same members) must not be attributed here.
            object?[] outsideHash = { null };
            beforePhase.Invoke(null, outsideHash);
            object Package(int bytes) => Activator.CreateInstance(package, new object[] { new byte[bytes] })!;
            afterHash.Invoke(null, new object?[] { Package(1000), outsideHash[0], null });
            var beforeEntry = (Array)drain.Invoke(book, null)!;
            Check(Count(Entry(beforeEntry, "CharacterSaveHash")) == 0, "GenerateHash outside the scope is not attributed");
            Check((long)packageBytesField.GetValue(null)! == 0, "GenerateHash outside the scope does not move the package-bytes gauge");

            // Enter the scope the way SavePlayerToDisk's own prefix/finalizer do.
            object?[] enter = { null };
            beforeSave.Invoke(null, enter);
            Check((bool)activeField.GetValue(null)!, "BeforeSave marks the calling thread active");

            object?[] cloudState = { null };
            beforePhase.Invoke(null, cloudState);
            afterCloudChecks.Invoke(null, new object?[] { cloudState[0], null });

            object?[] hashState = { null };
            beforePhase.Invoke(null, hashState);
            afterHash.Invoke(null, new object?[] { Package(42000), hashState[0], null });
            // A smaller second package (no timing state) must not lower the interval max.
            afterHash.Invoke(null, new object?[] { Package(500), outsideHash[0], null });

            object writer = FormatterServices.GetUninitializedObject(fileWriter);
            fileWriter.GetField("<m_fileSource>k__BackingField", Instance)!.SetValue(writer, Enum.Parse(fileSource, "Cloud"));
            afterWriterConstructed.Invoke(null, new object[] { writer });
            Check((bool)writeStartValidField.GetValue(null)!, "FileWriter construction marks a pending write span");
            Check((string)fileSourceField.GetValue(null)! == "cloud", "FileWriter's resolved file source is read cheaply after construction");

            object?[] writeState = { null };
            beforeWrite.Invoke(null, writeState);
            Check(!(bool)writeStartValidField.GetValue(null)!, "BeforeWrite consumes the pending marker so a second Finish cannot reuse it");
            afterWrite.Invoke(null, new object?[] { writeState[0], null });

            // A second Finish without a fresh construction (e.g. a defensive double call) must
            // not silently reuse the consumed marker.
            object?[] staleWrite = { null };
            beforeWrite.Invoke(null, staleWrite);
            Check(staleWrite[0]!.GetType().GetField("Session", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(staleWrite[0]) == null,
                "BeforeWrite without a preceding construction records nothing");

            object?[] replaceState = { null };
            beforePhase.Invoke(null, replaceState);
            afterReplace.Invoke(null, new object?[] { replaceState[0], null });

            object?[] backupState = { null };
            beforePhase.Invoke(null, backupState);
            afterBackup.Invoke(null, new object?[] { backupState[0], new InvalidOperationException("fixture") });

            afterSave.Invoke(null, enter);
            Check(!(bool)activeField.GetValue(null)!, "AfterSave restores the non-active state");

            var afterEntries = (Array)drain.Invoke(book, null)!;
            Check(Count(Entry(afterEntries, "CharacterSaveCloudChecks")) == 1 && Count(Entry(afterEntries, "CharacterSaveHash")) == 1 &&
                Count(Entry(afterEntries, "CharacterSaveWrite")) == 1 && Count(Entry(afterEntries, "CharacterSaveReplace")) == 1 &&
                Count(Entry(afterEntries, "CharacterSaveBackup")) == 1,
                "Each phase inside the scope is attributed exactly once, including the write span across GetArray/FileWriter");
            Check(Failed(Entry(afterEntries, "CharacterSaveBackup")) == 1, "A native exception during a phase is counted as a failed call, not swallowed");

            var numberType = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
            var textType = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
            var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(numberType))!;
            var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(textType))!;
            sample.Invoke(null, new object[] { gauges, labels });
            double GaugeValue(string name) => (double)numberType.GetProperty("Value")!.GetValue(
                gauges.Cast<object>().Single(g => (string)numberType.GetProperty("Name")!.GetValue(g)! == name))!;
            string LabelValue(string name) => (string)textType.GetProperty("Value")!.GetValue(
                labels.Cast<object>().Single(l => (string)textType.GetProperty("Name")!.GetValue(l)! == name))!;
            Check(GaugeValue("character_save_package_bytes") == 42000, "package-bytes gauge reports the interval max, not the last or smallest call");
            Check(LabelValue("character_save_file_source") == "cloud", "file-source label reports the resolved source");
            Check((long)packageBytesField.GetValue(null)! == 0, "Sample drains the package-bytes gauge for the next interval");
            Check((string)fileSourceField.GetValue(null)! == "none", "Sample resets the file-source label for the next interval");

            reset.Invoke(null, null);
            Check((long)probeFailuresField.GetValue(null)! == 0, "Reset clears probe failures");
        }
        finally
        {
            currentSession.SetValue(null, savedSession);
            activeField.SetValue(null, savedActive);
            setInstalled.Invoke(null, new[] { savedInstalled });
        }

        Console.WriteLine("CharacterSaveDisk: " + checks + " checks; no mount, hash, disk write or real save invoked.");
        return checks;
    }

    private static bool CallsDirectly(MethodInfo caller, MethodInfo target)
    {
        byte[]? body = caller.GetMethodBody()?.GetILAsByteArray();
        if (body == null) return false;
        for (int i = 0; i + 4 < body.Length; i++)
        {
            if (body[i] != OpCodes.Call.Value && body[i] != OpCodes.Callvirt.Value) continue;
            try { if (caller.Module.ResolveMethod(BitConverter.ToInt32(body, i + 1)) == target) return true; }
            catch { }
        }
        return false;
    }
}
