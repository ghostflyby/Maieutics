using System.Runtime.InteropServices;

namespace Maieutics.Processes;

/// <summary>Prevents a broad host filesystem view from connecting to host Unix sockets.
/// Private socket pairs remain usable for IPC between sandboxed processes.</summary>
internal static class LinuxSandboxSocketFilter
{
    internal static void Write(string file)
    {
        var (arch, socket) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => (0xc000003eU, 41U),
            Architecture.Arm64 => (0xc00000b7U, 198U),
            _ => throw new NotSupportedException("Broad filesystem reads require Linux x64 or arm64.")
        };

        const ushort loadWord = 0x20;
        const ushort jumpEqual = 0x15;
        const ushort jumpGreaterOrEqual = 0x35;
        const ushort ret = 0x06;
        const uint allow = 0x7fff0000;
        const uint deny = 0x00050001; // SECCOMP_RET_ERRNO | EPERM
        const uint kill = 0x80000000;

        using var writer = new BinaryWriter(File.Create(file));
        Instruction(loadWord, 4); // seccomp_data.arch
        Instruction(jumpEqual, arch, whenTrue: 1);
        Instruction(ret, kill);
        Instruction(loadWord, 0); // seccomp_data.nr
        Instruction(jumpGreaterOrEqual, 0x40000000, whenFalse: 1); // x32 ABI
        Instruction(ret, deny);

        // io_uring can create sockets outside the normal socket syscall path.
        for (uint syscall = 425; syscall <= 427; syscall++)
        {
            Instruction(jumpEqual, syscall, whenFalse: 1);
            Instruction(ret, deny);
        }

        Instruction(jumpEqual, socket, whenFalse: 3);
        Instruction(loadWord, 16); // seccomp_data.args[0], socket domain
        Instruction(jumpEqual, 1, whenFalse: 1); // AF_UNIX
        Instruction(ret, deny);
        Instruction(ret, allow);

        void Instruction(ushort code, uint operand, byte whenTrue = 0, byte whenFalse = 0)
        {
            writer.Write(code);
            writer.Write(whenTrue);
            writer.Write(whenFalse);
            writer.Write(operand);
        }
    }
}
