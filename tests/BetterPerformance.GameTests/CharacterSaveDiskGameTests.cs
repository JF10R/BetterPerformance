using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mono.Cecil;
using Cil = Mono.Cecil.Cil;

// Verifies the CharacterSaveToDisk phase probes against the installed assemblies and
// exercises the private hook methods directly with a synthetic session. The targets span
// assembly_valheim (PlayerProfile, SaveSystem, ZPackage, ZNet), assembly_utils (FileWriter,
// FileHelpers) and Splatform.dll (CloudStorageFileGrouping); none of the last two are
// referenced by the plugin project at compile time, matching CloudWriteOptimization's own
// resolution. Never mounts cloud storage, hashes anything, writes a file, or calls the real
// character-save path. The native call order that names each derived gap is pinned here.
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

        // --- Whole-save breakdown targets, read from metadata: Player cannot be type-loaded by
        // the standalone .NET Framework verifier, so signatures and call order come from Cecil. ---
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDirectory);
        using var module = ModuleDefinition.ReadModule(game.Location, new ReaderParameters { AssemblyResolver = resolver });
        string Key(MethodReference m) => m.DeclaringType.FullName + "::" + m.Name + "(" + string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
        MethodDefinition Def(string type, string key) => module.GetType(type)?.Methods.SingleOrDefault(m => Key(m) == key)
            ?? throw new InvalidOperationException("CharacterSaveDisk: " + key + " is missing.");
        FieldDefinition Field(string type, string name) => module.GetType(type)?.Fields.SingleOrDefault(f => f.Name == name)
            ?? throw new InvalidOperationException("CharacterSaveDisk: " + type + "." + name + " is missing.");
        List<string> CallKeys(MethodDefinition method) => method.Body.Instructions
            .Where(i => (i.OpCode.Code == Cil.Code.Call || i.OpCode.Code == Cil.Code.Callvirt || i.OpCode.Code == Cil.Code.Newobj) && i.Operand is MethodReference)
            .Select(i => Key((MethodReference)i.Operand)).ToList();
        bool InOrder(List<string> calls, params string[] sequence)
        {
            int at = -1;
            foreach (string target in sequence)
            {
                at = calls.FindIndex(at + 1, call => call == target);
                if (at < 0) return false;
            }
            return true;
        }
        int CountOf(List<string> calls, string target) => calls.Count(call => call == target);

        const string DiskKey = "PlayerProfile::SavePlayerToDisk()";
        const string CloudKey = "SaveSystem::PreSaveCloudChecksAndOperations(System.String,SaveDataType,FileHelpers/FileSource&,System.Boolean&,System.Boolean&,System.Int32,World)";
        const string HashKey = "ZPackage::GenerateHash()", GetArrayKey = "ZPackage::GetArray()";
        const string CtorKey = "FileWriter::.ctor(System.String,Splatform.CloudStorageFileGrouping,FileHelpers/FileHelperType,FileHelpers/FileSource)";
        const string FinishKey = "FileWriter::Finish()", InvalidateKey = "SaveSystem::InvalidateCache(SaveDataType)";
        const string ReplaceKey = "FileHelpers::ReplaceOldFile(System.String,System.String,System.String,Splatform.CloudStorageFileGrouping,FileHelpers/FileSource)";
        const string AutoBackupKey = "ZNet::ConsiderAutoBackup(System.String,SaveDataType,System.DateTime)";
        const string ProfileKey = "Game::SavePlayerProfile(System.Boolean,System.Boolean)", ProfileSaveKey = "PlayerProfile::Save()";
        const string PlayerDataKey = "PlayerProfile::SavePlayerData(Player)", PlayerSaveKey = "Player::Save(ZPackage)";
        const string InventoryKey = "Inventory::Save(ZPackage)", SkillsKey = "Skills::Save(ZPackage)";
        const string MapDataKey = "Minimap::SaveMapData()", GetMapKey = "Minimap::GetMapData()", SetMapKey = "PlayerProfile::SetMapData(System.Byte[])";
        const string TryGetKey = "SaveSystem::TryGetSaveByName(System.String,SaveDataType,SaveWithBackups&)";
        const string CopyKey = "SaveSystem::Copy(SaveFile,System.String,FileHelpers/FileSource)";
        const string CollectionTryGetKey = "SaveCollection::TryGetSaveByName(System.String,SaveWithBackups&)";
        const string EnsureLoadedKey = "SaveCollection::EnsureLoaded()", ReloadKey = "SaveCollection::Reload()";
        const string GetFilesKey = "FileHelpers::GetFiles(FileHelpers/FileSource,System.String,System.String,System.String)";

        MethodDefinition profileDef = Def("Game", ProfileKey);
        Check(profileDef.ReturnType.FullName == "System.Void" && !profileDef.IsStatic &&
            profileDef.Parameters.Select(p => p.Name).SequenceEqual(new[] { "setLogoutPoint", "isFromRpc" }),
            "Game.SavePlayerProfile keeps (setLogoutPoint, isFromRpc), bound by name");
        FieldDefinition shutting = Field("Game", "m_shuttingDown"), timer = Field("Game", "m_saveTimer"), interval = Field("Game", "m_saveInterval");
        Check(shutting.FieldType.FullName == "System.Boolean" && !shutting.IsStatic && timer.FieldType.FullName == "System.Single" &&
            timer.IsPublic && !timer.IsStatic && interval.FieldType.FullName == "System.Single" && interval.IsPublic && interval.IsStatic,
            "trigger fields keep their types and access");
        MethodDefinition playerDataDef = Def("PlayerProfile", PlayerDataKey);
        FieldDefinition playerDataFieldDef = Field("PlayerProfile", "m_playerData");
        Check(playerDataDef.ReturnType.FullName == "System.Void" && !playerDataDef.IsStatic && playerDataFieldDef.FieldType.FullName == "System.Byte[]",
            "SavePlayerData is a void instance method that stores m_playerData (byte[])");
        Check(Def("Inventory", InventoryKey).ReturnType.FullName == "System.Void" && Def("Skills", SkillsKey).ReturnType.FullName == "System.Void",
            "Inventory/Skills.Save keep their void shape");
        Check(Def("Minimap", MapDataKey).ReturnType.FullName == "System.Void", "Minimap.SaveMapData keeps its void shape");
        Check(Def("PlayerProfile", SetMapKey).Parameters[0].Name == "data", "PlayerProfile.SetMapData keeps its 'data' parameter, bound by name");
        Check(Def("SaveCollection", ReloadKey).ReturnType.FullName == "System.Void" && Field("SaveCollection", "m_dataType").FieldType.FullName == "SaveDataType",
            "SaveCollection.Reload and its m_dataType discriminator");
        Check(Def("SaveSystem", CopyKey).ReturnType.FullName == "System.Boolean" && Def("SaveSystem", CopyKey).IsStatic,
            "SaveSystem.Copy(SaveFile, string, FileSource) returns bool");

        // Native call order: each derived gap is named after what runs between two hooked calls.
        List<string> disk = CallKeys(Def("PlayerProfile", DiskKey));
        Check(InOrder(disk, CloudKey, HashKey, GetArrayKey, CtorKey, FinishKey, InvalidateKey, ReplaceKey, InvalidateKey, AutoBackupKey),
            "SavePlayerToDisk order: cloud checks, hash, GetArray, FileWriter, Finish, invalidate, replace, invalidate, auto-backup");
        foreach (string once in new[] { CloudKey, HashKey, GetArrayKey, CtorKey, FinishKey, ReplaceKey, AutoBackupKey })
            Check(CountOf(disk, once) == 1, once + ": called exactly once by SavePlayerToDisk");
        int hashAt = disk.IndexOf(HashKey), ctorAt = disk.IndexOf(CtorKey), finishAt = disk.IndexOf(FinishKey);
        Check(disk.Skip(hashAt + 1).Take(ctorAt - hashAt - 1).SequenceEqual(new[] { GetArrayKey }),
            "GetArray gap: nothing but the second GetArray runs between GenerateHash and the FileWriter");
        Check(disk.Skip(ctorAt + 1).Take(finishAt - ctorAt - 1).All(call => call.StartsWith("System.IO.BinaryWriter::Write(", StringComparison.Ordinal)),
            "BufferWrite gap: only BinaryWriter.Write calls run between construction and Finish");
        List<string> profileCalls = CallKeys(profileDef);
        Check(InOrder(profileCalls, PlayerDataKey, MapDataKey, ProfileSaveKey) && CountOf(profileCalls, PlayerDataKey) == 1 &&
            CountOf(profileCalls, MapDataKey) == 1 && CountOf(profileCalls, ProfileSaveKey) == 1,
            "SavePlayerProfile order: player data, map data, then PlayerProfile.Save, once each");
        Check(CallKeys(Def("PlayerProfile", ProfileSaveKey)).Contains(DiskKey), "PlayerProfile.Save reaches SavePlayerToDisk directly");
        Check(InOrder(CallKeys(playerDataDef), PlayerSaveKey, GetArrayKey), "SavePlayerData: Player.Save then GetArray");
        List<string> playerCalls = CallKeys(Def("Player", PlayerSaveKey));
        Check(CountOf(playerCalls, InventoryKey) == 1 && CountOf(playerCalls, SkillsKey) == 1, "Player.Save writes inventory and skills once each");
        Check(InOrder(CallKeys(Def("Minimap", MapDataKey)), GetMapKey, SetMapKey), "SaveMapData: GetMapData then PlayerProfile.SetMapData");
        string considerBackupKey = CallKeys(Def("ZNet", AutoBackupKey)).SingleOrDefault(k => k.StartsWith("SaveSystem::ConsiderBackup(", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("CharacterSaveDisk: ConsiderAutoBackup no longer delegates to SaveSystem.ConsiderBackup.");
        Check(InOrder(CallKeys(Def("SaveSystem", considerBackupKey)), TryGetKey, CopyKey), "ConsiderBackup looks the save up (catalog) before copying it");
        Check(CallKeys(Def("SaveCollection", CollectionTryGetKey)).Contains(EnsureLoadedKey) && CallKeys(Def("SaveCollection", EnsureLoadedKey)).Contains(ReloadKey),
            "a catalog lookup reloads through EnsureLoaded -> Reload when invalidated");
        Check(module.GetType("SaveCollection").Methods.Where(m => m.Name.Contains("GetAllFilesInSource")).Any(m => CallKeys(m).Contains(GetFilesKey)),
            "Reload enumerates each source through FileHelpers.GetFiles");

        // Reflection handles for the runtime patch checks; null where this process cannot load the type.
        MethodBase? Loadable(Func<MethodBase?> find)
        {
            try { return find(); }
            catch (TypeLoadException) { return null; }
        }
        Type gameType = game.GetType("Game", true)!;
        Type saveCollection = game.GetType("SaveCollection", true)!;
        MethodBase? saveProfile = Loadable(() => gameType.GetMethod("SavePlayerProfile", Instance, null, new[] { typeof(bool), typeof(bool) }, null));
        MethodBase? savePlayerData = Loadable(() => playerProfile.GetMethod("SavePlayerData", Instance, null, new[] { game.GetType("Player", true)! }, null));
        MethodBase? inventorySave = Loadable(() => game.GetType("Inventory", true)!.GetMethod("Save", Instance, null, new[] { package }, null));
        MethodBase? skillsSave = Loadable(() => game.GetType("Skills", true)!.GetMethod("Save", Instance, null, new[] { package }, null));
        MethodBase? saveMapData = Loadable(() => game.GetType("Minimap", true)!.GetMethod("SaveMapData", Instance, null, Type.EmptyTypes, null));
        MethodBase? getMapData = Loadable(() => game.GetType("Minimap", true)!.GetMethod("GetMapData", Instance, null, Type.EmptyTypes, null));
        MethodBase? setMapData = Loadable(() => playerProfile.GetMethod("SetMapData", Instance, null, new[] { typeof(byte[]) }, null));
        MethodBase? reload = Loadable(() => saveCollection.GetMethod("Reload", Instance, null, Type.EmptyTypes, null));
        MethodBase? getFiles = Loadable(() => fileHelpers.GetMethod("GetFiles", Static, null, new[] { fileSource, typeof(string), typeof(string), typeof(string) }, null));
        MethodBase? copy = Loadable(() => saveSystem.GetMethod("Copy", Static, null, new[] { game.GetType("SaveFile", true)!, typeof(string), fileSource }, null));
        FieldInfo saveTimer = gameType.GetField("m_saveTimer", Instance)!;
        FieldInfo saveInterval = gameType.GetField("m_saveInterval", Static)!;
        FieldInfo playerDataField = playerProfile.GetField("m_playerData", Instance)!;
        var optionalProbes = new (string Name, MethodBase? Target, bool Prefix, bool Finalizer, bool Postfix)[]
        {
            ("profile", saveProfile, true, true, false), ("player_data", savePlayerData, true, true, false),
            ("inventory", inventorySave, true, true, false), ("skills", skillsSave, true, true, false),
            ("map_data", saveMapData, true, true, false), ("map_data_bytes", setMapData, true, false, false),
            ("catalog_reload", reload, true, true, false), ("catalog_files", getFiles, false, false, true),
            ("backup_copy", copy, true, true, false)
        };

        Type metricType = plugin.GetType("BetterPerformance.Core.Metric", true)!;
        foreach (string name in new[] { "CharacterSaveCloudChecks", "CharacterSaveHash", "CharacterSaveWrite", "CharacterSaveReplace", "CharacterSaveBackup",
            "CharacterSavePlayerData", "CharacterSaveInventory", "CharacterSaveSkills", "CharacterSaveMapData", "CharacterSaveBuild",
            "CharacterSaveGetArray", "CharacterSaveOpen", "CharacterSaveBufferWrite", "CharacterSaveFinish", "CharacterSaveCatalogReload",
            "CharacterSaveBackupCopy", "CharacterSaveUnaccounted", "CharacterSaveToDiskUnaccounted" })
            Check(Enum.IsDefined(metricType, name), name + ": metric is exportable");

        Type telemetry = plugin.GetType("BetterPerformance.CharacterSaveDiskTelemetry", true)!;
        MethodInfo install = telemetry.GetMethod("Install", Static)!;
        MethodInfo uninstall = telemetry.GetMethod("Uninstall", Static)!;
        PropertyInfo installedProperty = telemetry.GetProperty("Installed", Static)!;
        PropertyInfo statusProperty = telemetry.GetProperty("Status", Static)!;
        var missingProbes = (List<string>)telemetry.GetField("MissingProbes", Static)!.GetValue(null)!;
        Harmony patches = (Harmony)telemetry.GetField("Patches", Static)!.GetValue(null)!;
        string pluginId = (string)plugin.GetType("BetterPerformance.Plugin", true)!.GetField("PluginId")!.GetRawConstantValue()!;

        ConfigFile NewConfig() => new ConfigFile(
            Path.Combine(Path.GetTempPath(), "bp-character-save-disk-" + Guid.NewGuid().ToString("N") + ".cfg"), false) { SaveOnConfigSet = false };

        var allTargets = new MethodBase?[] { saveToDisk, cloudChecks, generateHash, getArray, writerCtor, finish, replace, autoBackup, getMapData }
            .Concat(optionalProbes.Select(p => p.Target)).Where(m => m != null).Select(m => m!).ToArray();

        // Config off: no patch anywhere, no half-installed state.
        var disabledConfig = NewConfig();
        disabledConfig.Bind("Diagnostics", "CharacterSaveDiskPhases", false);
        install.Invoke(null, new object[] { disabledConfig, new ManualLogSource("CharacterSaveDiskVerification") });
        Check(!(bool)installedProperty.GetValue(null)!, "disabled configuration installs nothing");
        Check((string)statusProperty.GetValue(null)! == "disabled", "disabled configuration reports disabled");
        foreach (MethodBase method in allTargets)
            Check(Harmony.GetPatchInfo(method)?.Owners.Contains(patches.Id) != true, method.Name + ": disabled configuration leaves it unpatched");

        // Default configuration installs every phase probe with the exact expected shape.
        install.Invoke(null, new object[] { NewConfig(), new ManualLogSource("CharacterSaveDiskVerification") });
        var missingAtInstall = new List<string>(missingProbes);
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
            Check(getMapData == null || Harmony.GetPatchInfo(getMapData)?.Owners.Contains(patches.Id) != true, "GetMapData: left to the map modules; bytes read at SetMapData");
            CheckPatch(writerCtor, true, false, true, "FileWriter constructor");
            CheckPatch(finish, true, true, false, "Finish");
            CheckPatch(replace, true, true, false, "ReplaceOldFile");
            CheckPatch(autoBackup, true, true, false, "ConsiderAutoBackup");
            // Each optional probe is either installed with its exact shape, or missing for a reason this
            // process can prove: its type cannot load here, or patching it hits the offline JIT limit.
            foreach (var probe in optionalProbes)
            {
                if (!missingProbes.Contains(probe.Name))
                {
                    Check(probe.Target != null, probe.Name + ": installed, so its target must load here");
                    CheckPatch(probe.Target!, probe.Prefix, probe.Finalizer, probe.Postfix, probe.Name);
                    continue;
                }
                if (probe.Target == null)
                {
                    Console.WriteLine("STATIC ONLY CharacterSaveDisk " + probe.Name + ": metadata contract verified; offline CLR cannot load its types");
                    continue;
                }
                var trial = new Harmony("BetterPerformance.offline.charactersave." + probe.Name);
                try
                {
                    trial.Patch(probe.Target, prefix: new HarmonyMethod(typeof(CharacterSaveDiskGameTests), nameof(NoOp)));
                    try { trial.UnpatchSelf(); } catch { }
                    Check(false, probe.Name + ": missing although its target patches in this process (a real contract break)");
                }
                catch (HarmonyException exception) when (OfflineUnityFailure(exception))
                {
                    Console.WriteLine("STATIC ONLY CharacterSaveDisk " + probe.Name + ": metadata contract verified; offline .NET Framework cannot JIT the patched body");
                }
                catch (HarmonyException exception)
                {
                    Check(false, probe.Name + ": missing, and its target fails to patch for a reason other than the offline JIT limit (" +
                        (exception.InnerException ?? exception).Message + ")");
                }
            }
        }
        finally { uninstall.Invoke(null, null); }
        // A probe that failed to install offline can leave stale patch state Harmony cannot rewrite
        // here; every probe that did install must be removed regardless.
        var failedTargets = optionalProbes.Where(p => missingAtInstall.Contains(p.Name) && p.Target != null).Select(p => p.Target!).ToList();
        foreach (MethodBase method in allTargets.Where(m => !failedTargets.Contains(m)))
            Check(Harmony.GetPatchInfo(method)?.Owners.Contains(patches.Id) != true, method.Name + ": uninstall removes this module's patch");
        string cleared = (string)statusProperty.GetValue(null)!;
        Check(cleared == "disabled" || cleared.StartsWith("unpatch_failed_", StringComparison.Ordinal), "uninstall reports its outcome (actual=" + cleared + ")");

        // --- Direct hook-method scenario checks: no game call, no file, no hash. ---
        FieldInfo activeField = telemetry.GetField("active", Static)!;
        FieldInfo profileActiveField = telemetry.GetField("profileActive", Static)!;
        FieldInfo profileProbeField = telemetry.GetField("profileProbe", Static)!;
        FieldInfo writeStartValidField = telemetry.GetField("writeStartValid", Static)!;
        FieldInfo packageBytesField = telemetry.GetField("packageBytes", Static)!;
        FieldInfo probeFailuresField = telemetry.GetField("probeFailures", Static)!;
        FieldInfo fileSourceField = telemetry.GetField("fileSource", Static)!;
        MethodInfo Hook(string name) => telemetry.GetMethod(name, Static) ?? throw new InvalidOperationException("CharacterSaveDisk: hook " + name + " is missing.");
        MethodInfo beforeSave = Hook("BeforeSave"), afterSave = Hook("AfterSave"), beforePhase = Hook("BeforePhase");
        MethodInfo afterCloudChecks = Hook("AfterCloudChecks"), beforeHash = Hook("BeforeHash"), afterHash = Hook("AfterHash");
        MethodInfo afterReplace = Hook("AfterReplace"), afterBackup = Hook("AfterBackup");
        MethodInfo beforeWrite = Hook("BeforeWrite"), afterWrite = Hook("AfterWrite");
        MethodInfo beforeWriterConstructed = Hook("BeforeWriterConstructed"), afterWriterConstructed = Hook("AfterWriterConstructed");
        MethodInfo beforeProfile = Hook("BeforeProfile"), afterProfile = Hook("AfterProfile");
        MethodInfo beforePlayerData = Hook("BeforePlayerData"), afterPlayerData = Hook("AfterPlayerData");
        MethodInfo beforeNested = Hook("BeforeNested"), afterInventory = Hook("AfterInventory"), afterSkills = Hook("AfterSkills");
        MethodInfo beforeMapData = Hook("BeforeMapData"), afterMapData = Hook("AfterMapData"), beforeSetMapData = Hook("BeforeSetMapData");
        MethodInfo beforeReload = Hook("BeforeReload"), afterReload = Hook("AfterReload"), afterGetFiles = Hook("AfterGetFiles");
        MethodInfo afterBackupCopy = Hook("AfterBackupCopy");
        MethodInfo sample = Hook("Sample"), reset = Hook("Reset");

        Type timingHooks = plugin.GetType("BetterPerformance.TimingHooks", true)!;
        FieldInfo currentSession = timingHooks.GetField("Current", Static)!;
        object? savedSession = currentSession.GetValue(null);
        object? savedActive = activeField.GetValue(null);
        object? savedProfileProbe = profileProbeField.GetValue(null);
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
            object? State(MethodInfo prefix, params object?[] arguments)
            {
                object?[] call = arguments.Concat(new object?[] { null }).ToArray();
                prefix.Invoke(null, call);
                return call[call.Length - 1];
            }

            var numberType = plugin.GetType("BetterPerformance.Core.NumberValue", true)!;
            var textType = plugin.GetType("BetterPerformance.Core.TextValue", true)!;
            (Func<string, double> Gauge, Func<string, string> Label) Sample()
            {
                var gauges = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(numberType))!;
                var labels = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(textType))!;
                sample.Invoke(null, new object[] { gauges, labels });
                return (name => (double)numberType.GetProperty("Value")!.GetValue(
                        gauges.Cast<object>().Single(g => (string)numberType.GetProperty("Name")!.GetValue(g)! == name))!,
                    name => (string)textType.GetProperty("Value")!.GetValue(
                        labels.Cast<object>().Single(l => (string)textType.GetProperty("Name")!.GetValue(l)! == name))!);
            }

            reset.Invoke(null, null);
            activeField.SetValue(null, false);
            currentSession.SetValue(null, session);

            // Outside the SavePlayerToDisk scope, a foreign GenerateHash/GetArray/Finish
            // (a concurrent world save uses the same members) must not be attributed here.
            object? outsideHash = State(beforeHash);
            object Package(int bytes) => Activator.CreateInstance(package, new object[] { new byte[bytes] })!;
            afterHash.Invoke(null, new object?[] { Package(1000), outsideHash, null });
            var beforeEntry = (Array)drain.Invoke(book, null)!;
            Check(Count(Entry(beforeEntry, "CharacterSaveHash")) == 0, "GenerateHash outside the scope is not attributed");
            Check((long)packageBytesField.GetValue(null)! == 0, "GenerateHash outside the scope does not move the package-bytes gauge");

            // Enter the scope the way SavePlayerToDisk's own prefix/finalizer do (no profile scope: a direct disk save).
            object?[] enter = { null };
            beforeSave.Invoke(null, enter);
            Check((bool)activeField.GetValue(null)!, "BeforeSave marks the calling thread active");

            afterCloudChecks.Invoke(null, new object?[] { State(beforePhase), null });

            object? hashState = State(beforeHash);
            afterHash.Invoke(null, new object?[] { Package(42000), hashState, null });
            // A smaller second package (no timing state) must not lower the interval max.
            afterHash.Invoke(null, new object?[] { Package(500), outsideHash, null });

            object writer = FormatterServices.GetUninitializedObject(fileWriter);
            fileWriter.GetField("<m_fileSource>k__BackingField", Instance)!.SetValue(writer, Enum.Parse(fileSource, "Cloud"));
            afterWriterConstructed.Invoke(null, new object?[] { writer, State(beforeWriterConstructed) });
            Check((bool)writeStartValidField.GetValue(null)!, "FileWriter construction marks a pending write span");
            Check((string)fileSourceField.GetValue(null)! == "cloud", "FileWriter's resolved file source is read cheaply after construction");

            object? writeState = State(beforeWrite);
            Check(!(bool)writeStartValidField.GetValue(null)!, "BeforeWrite consumes the pending marker so a second Finish cannot reuse it");
            afterWrite.Invoke(null, new object?[] { writeState, null });

            // A second Finish without a fresh construction (e.g. a defensive double call) must
            // not silently reuse the consumed marker.
            object? staleWrite = State(beforeWrite);
            Check(staleWrite!.GetType().GetField("Session", Instance)!.GetValue(staleWrite) == null &&
                !(bool)staleWrite.GetType().GetField("Valid", Instance)!.GetValue(staleWrite)!,
                "BeforeWrite without a preceding construction records nothing");
            afterWrite.Invoke(null, new object?[] { staleWrite, null });

            afterReplace.Invoke(null, new object?[] { State(beforePhase), null });
            afterBackup.Invoke(null, new object?[] { State(beforePhase), new InvalidOperationException("fixture") });

            afterSave.Invoke(null, enter);
            Check(!(bool)activeField.GetValue(null)!, "AfterSave restores the non-active state");

            var afterEntries = (Array)drain.Invoke(book, null)!;
            foreach (string name in new[] { "CharacterSaveCloudChecks", "CharacterSaveHash", "CharacterSaveWrite", "CharacterSaveReplace",
                "CharacterSaveBackup", "CharacterSaveBuild", "CharacterSaveGetArray", "CharacterSaveOpen", "CharacterSaveBufferWrite",
                "CharacterSaveFinish", "CharacterSaveToDiskUnaccounted" })
                Check(Count(Entry(afterEntries, name)) == 1, name + ": attributed exactly once inside the disk scope");
            Check(Count(Entry(afterEntries, "CharacterSaveUnaccounted")) == 0, "a direct disk save has no profile-level remainder");
            Check(Failed(Entry(afterEntries, "CharacterSaveBackup")) == 1, "A native exception during a phase is counted as a failed call, not swallowed");

            var first = Sample();
            Check(first.Gauge("character_save_package_bytes") == 42000, "package-bytes gauge reports the interval max, not the last or smallest call");
            Check(first.Label("character_save_file_source") == "cloud", "file-source label reports the resolved source");
            Check(first.Gauge("character_save_direct_disk_saves") == 1 && first.Gauge("character_saves") == 0,
                "a disk save outside SavePlayerProfile is counted as direct, not as a triggered save");
            Check((long)packageBytesField.GetValue(null)! == 0, "Sample drains the package-bytes gauge for the next interval");
            Check((string)fileSourceField.GetValue(null)! == "none", "Sample resets the file-source label for the next interval");

            // --- A whole periodic save through every optional probe. ---
            profileProbeField.SetValue(null, true);
            object gameInstance = FormatterServices.GetUninitializedObject(gameType);
            saveTimer!.SetValue(gameInstance, (float)saveInterval!.GetValue(null)! + 1f);
            object profileInstance = FormatterServices.GetUninitializedObject(playerProfile);
            playerDataField!.SetValue(profileInstance, new byte[1234]);
            object characters = Activator.CreateInstance(saveCollection, Enum.Parse(saveDataType, "Character"))!;
            object worlds = Activator.CreateInstance(saveCollection, Enum.Parse(saveDataType, "World"))!;

            // Container saves and map writes outside the scopes stay unattributed.
            afterInventory.Invoke(null, new object?[] { State(beforeNested), null });
            beforeSetMapData.Invoke(null, new object[] { new byte[99999] });

            // A save the native body skips (DontSaveCharacter, HardSaveBlock) never reaches the disk.
            object? blocked = State(beforeProfile, gameInstance, true, false);
            afterProfile.Invoke(null, new[] { blocked });
            Check(!(bool)profileActiveField.GetValue(null)!, "AfterProfile closes the scope");

            object? profileState = State(beforeProfile, gameInstance, true, false);
            Check((bool)profileActiveField.GetValue(null)!, "BeforeProfile opens the whole-save scope");
            object? playerState = State(beforePlayerData);
            afterInventory.Invoke(null, new object?[] { State(beforeNested), null });
            afterSkills.Invoke(null, new object?[] { State(beforeNested), null });
            afterPlayerData.Invoke(null, new object?[] { profileInstance, playerState, null });
            afterInventory.Invoke(null, new object?[] { State(beforeNested), null });
            object? mapState = State(beforeMapData);
            beforeSetMapData.Invoke(null, new object[] { new byte[5555] });
            afterMapData.Invoke(null, new object?[] { mapState, null });

            object?[] enterDisk = { null };
            beforeSave.Invoke(null, enterDisk);
            afterCloudChecks.Invoke(null, new object?[] { State(beforePhase), null });
            afterHash.Invoke(null, new object?[] { Package(3000), State(beforeHash), null });
            afterWriterConstructed.Invoke(null, new object?[] { writer, State(beforeWriterConstructed) });
            afterWrite.Invoke(null, new object?[] { State(beforeWrite), null });
            afterReplace.Invoke(null, new object?[] { State(beforePhase), null });
            object? backupState = State(beforePhase);
            object? worldReload = State(beforeReload, worlds);
            afterGetFiles.Invoke(null, new object[] { new string[50] });
            afterReload.Invoke(null, new object?[] { worldReload, null });
            object? reloadState = State(beforeReload, characters);
            afterGetFiles.Invoke(null, new object[] { new string[7] });
            afterGetFiles.Invoke(null, new object?[] { null });
            afterGetFiles.Invoke(null, new object[] { new string[3] });
            afterReload.Invoke(null, new object?[] { reloadState, null });
            afterGetFiles.Invoke(null, new object[] { new string[40] });
            afterBackupCopy.Invoke(null, new object?[] { State(beforePhase), null });
            afterBackup.Invoke(null, new object?[] { backupState, null });
            afterSave.Invoke(null, enterDisk);
            afterProfile.Invoke(null, new[] { profileState });
            Check(!(bool)profileActiveField.GetValue(null)! && !(bool)activeField.GetValue(null)!, "both scopes close after the save");

            var saveEntries = (Array)drain.Invoke(book, null)!;
            foreach (string name in new[] { "CharacterSavePlayerData", "CharacterSaveSkills", "CharacterSaveMapData", "CharacterSaveBuild",
                "CharacterSaveGetArray", "CharacterSaveOpen", "CharacterSaveBufferWrite", "CharacterSaveFinish", "CharacterSaveCatalogReload",
                "CharacterSaveBackupCopy", "CharacterSaveUnaccounted", "CharacterSaveToDiskUnaccounted" })
                Check(Count(Entry(saveEntries, name)) == 1, name + ": attributed exactly once in a whole save");
            Check(Count(Entry(saveEntries, "CharacterSaveInventory")) == 1,
                "Inventory.Save counts only inside SavePlayerData, not for a container before or after");
            Check(Count(Entry(saveEntries, "CharacterSaveCatalogReload")) == 1, "a world-catalog reload is not attributed to the character save");

            var second = Sample();
            Check(second.Gauge("character_saves") == 1 && second.Gauge("character_save_trigger_periodic") == 1,
                "one periodic save counted; the blocked call counts nothing");
            Check(second.Gauge("character_save_direct_disk_saves") == 0, "a disk save inside SavePlayerProfile is not direct");
            Check(second.Gauge("character_save_player_data_bytes") == 1234, "player-data bytes read from m_playerData after SavePlayerData");
            Check(second.Gauge("character_save_map_data_bytes") == 5555, "map bytes read only inside SaveMapData");
            Check(second.Gauge("character_save_catalog_files") == 10, "catalog files counted only inside the character reload");
            Check(second.Label("character_save_probes_missing") == "none", "probes-missing reads none when no probe failed");

            reset.Invoke(null, null);
            Check((long)probeFailuresField.GetValue(null)! == 0, "Reset clears probe failures");
        }
        finally
        {
            currentSession.SetValue(null, savedSession);
            activeField.SetValue(null, savedActive);
            profileActiveField.SetValue(null, false);
            profileProbeField.SetValue(null, savedProfileProbe);
            setInstalled.Invoke(null, new[] { savedInstalled });
        }

        Console.WriteLine("CharacterSaveDisk: " + checks + " checks; no mount, hash, disk write or real save invoked.");
        return checks;
    }

    private static void NoOp() { }

    // Program.cs's predicate for the TimingHooks probes (Unity interfaces and ECalls the standalone
    // .NET Framework cannot JIT), plus a BCL member Unity's profile has and net472 lacks
    // (SaveCollection.Reload deconstructs KeyValuePair).
    private static bool OfflineUnityFailure(HarmonyException exception) =>
        (exception.InnerException is TypeLoadException load && load.Message.Contains("Non-abstract, non-.cctor method in an interface")) ||
        (exception.InnerException is System.Security.SecurityException security && security.Message.Contains("ECall methods must be packaged into a system module")) ||
        (exception.InnerException is MissingMethodException missing && missing.Message.StartsWith("Method not found: '", StringComparison.Ordinal) &&
            missing.Message.Contains(" System."));

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
