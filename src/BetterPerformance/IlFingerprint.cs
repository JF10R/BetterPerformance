using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;

namespace BetterPerformance
{
    // A raw SHA-256 of a method's IL bytes embeds metadata tokens, which renumber whenever
    // anything else in the assembly changes: the 1.0.14 and 1.0.15 game updates each broke
    // such pins while the pinned bodies stayed byte-for-byte identical in length and logic.
    // This fingerprint hashes the opcode stream plus every operand, but replaces each
    // member/type/string token with the resolved member's full name, so it is stable across
    // token churn and still changes on any opcode, constant, branch target, or referenced
    // member. It is a contract check, not a security primitive.
    internal static class IlFingerprint
    {
        private static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodes();

        internal static string Compute(MethodBase method)
        {
            byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) throw new InvalidOperationException("Method body is unreadable.");
            Type[] typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : Type.EmptyTypes;
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
            using (var sha = SHA256.Create())
            using (var stream = new System.IO.MemoryStream(il.Length * 4))
            {
                Append(stream, BitConverter.GetBytes(il.Length));
                for (int offset = 0; offset < il.Length;)
                {
                    int raw = il[offset++];
                    if (raw == 0xfe) raw = 0xfe00 | il[offset++];
                    if (!Opcodes.TryGetValue(unchecked((short)raw), out OpCode opcode))
                        throw new InvalidOperationException("Unknown opcode 0x" + raw.ToString("X") + " at " + offset + ".");
                    Append(stream, BitConverter.GetBytes(opcode.Value));
                    int length = OperandLength(opcode.OperandType, il, offset);
                    switch (opcode.OperandType)
                    {
                        case OperandType.InlineMethod:
                        case OperandType.InlineField:
                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                            Append(stream, Encoding.UTF8.GetBytes(Describe(method.Module, BitConverter.ToInt32(il, offset), typeArguments, methodArguments)));
                            break;
                        case OperandType.InlineString:
                            Append(stream, Encoding.UTF8.GetBytes(method.Module.ResolveString(BitConverter.ToInt32(il, offset))));
                            break;
                        default:
                            stream.Write(il, offset, length);
                            break;
                    }
                    offset += length;
                }
                return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }

        // Declaring type, name and signature: stable across builds, distinct across members.
        private static string Describe(Module module, int token, Type[] typeArguments, Type[] methodArguments)
        {
            MemberInfo member = module.ResolveMember(token, typeArguments, methodArguments)
                ?? throw new InvalidOperationException("Unresolvable member token.");
            string owner = member.DeclaringType?.FullName ?? "";
            return member is Type type ? "T:" + type.FullName : owner + "::" + member;
        }

        private static int OperandLength(OperandType type, byte[] il, int offset)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + 4 * BitConverter.ToInt32(il, offset);
                default: return 4;
            }
        }

        private static void Append(System.IO.Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);

        private static Dictionary<short, OpCode> BuildOpcodes()
        {
            var table = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.FieldType == typeof(OpCode)) { var opcode = (OpCode)field.GetValue(null)!; table[opcode.Value] = opcode; }
            return table;
        }
    }
}
