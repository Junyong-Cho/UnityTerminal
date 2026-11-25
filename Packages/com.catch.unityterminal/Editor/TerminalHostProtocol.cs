using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace UnityTerminal
{
    internal enum HostCommandKind : byte { PrimaryInput, InteractiveInput, StartInteractive, StopInteractive, Resize, ClearPrimary, Shutdown, SystemMessage }
    [Serializable]
    internal sealed class HostCommand
    {
        public long Sequence;
        public HostCommandKind Kind;
        public string Id = "", Text = "";
        public int Columns, Rows;
    }
    internal sealed class HostState
    {
        public int HostPid, PrimaryPid, InteractivePid, BufferVersion, ScreenVersion, PrimaryExitCode, InteractiveExitCode;
        public long HostStartTicks, Acknowledged;
        public bool PrimaryExited, InteractiveActive, InteractiveExited;
        public string InteractiveId = "", Failure = "", InteractiveFailure = "", Notice = "";
        public byte[] Buffer, Screen;
    }
    internal static class TerminalHostProtocol
    {
        internal const int Version = 1;
        private const int MaxFrame = 16 * 1024 * 1024;
        internal static void WriteFrame(Stream stream, Action<BinaryWriter> write)
        {
            using (var memory = new MemoryStream())
            {
                using (var writer = new BinaryWriter(memory, Encoding.UTF8, true)) { writer.Write(Version); write(writer); }
                if (memory.Length > MaxFrame) throw new InvalidDataException("Terminal message is too large.");
                byte[] size = BitConverter.GetBytes((int)memory.Length);
                stream.Write(size, 0, size.Length); memory.Position = 0; memory.CopyTo(stream); stream.Flush();
            }
        }
        internal static BinaryReader ReadFrame(Stream stream)
        {
            byte[] size = ReadExactly(stream, 4);
            int length = BitConverter.ToInt32(size, 0);
            if (length < 4 || length > MaxFrame) throw new InvalidDataException("Invalid terminal message length.");
            var reader = new BinaryReader(new MemoryStream(ReadExactly(stream, length)), Encoding.UTF8);
            if (reader.ReadInt32() != Version) { reader.Dispose(); throw new InvalidDataException("Terminal host protocol changed. Restart Terminal."); }
            return reader;
        }
        private static byte[] ReadExactly(Stream stream, int size)
        {
            var bytes = new byte[size]; int offset = 0;
            while (offset < size) { int count = stream.Read(bytes, offset, size - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
            return bytes;
        }
        internal static void WriteCommands(BinaryWriter writer, int bufferVersion, int screenVersion, string screenId, List<HostCommand> commands)
        {
            writer.Write(bufferVersion); writer.Write(screenVersion); writer.Write(screenId ?? ""); writer.Write(commands.Count);
            foreach (HostCommand command in commands)
            {
                writer.Write(command.Sequence); writer.Write((byte)command.Kind); writer.Write(command.Id); writer.Write(command.Text);
                writer.Write(command.Columns); writer.Write(command.Rows);
            }
        }
        internal static List<HostCommand> ReadCommands(BinaryReader reader, out int bufferVersion, out int screenVersion, out string screenId)
        {
            bufferVersion = reader.ReadInt32(); screenVersion = reader.ReadInt32(); screenId = reader.ReadString();
            int count = reader.ReadInt32();
            if (count < 0 || count > 1024) throw new InvalidDataException("Too many terminal commands.");
            var commands = new List<HostCommand>(); int characters = 0;
            for (int i = 0; i < count; i++)
            {
                var command = new HostCommand { Sequence = reader.ReadInt64(), Kind = (HostCommandKind)reader.ReadByte(), Id = reader.ReadString(),
                    Text = reader.ReadString(), Columns = reader.ReadInt32(), Rows = reader.ReadInt32() };
                characters += command.Text.Length;
                if (characters > 262144 || command.Id.Length > 128) throw new InvalidDataException("Terminal input is too large.");
                commands.Add(command);
            }
            return commands;
        }
        internal static void WriteState(BinaryWriter writer, HostState state)
        {
            writer.Write(state.HostPid); writer.Write(state.HostStartTicks); writer.Write(state.PrimaryPid); writer.Write(state.InteractivePid);
            writer.Write(state.Acknowledged); writer.Write(state.BufferVersion); writer.Write(state.ScreenVersion);
            writer.Write(state.PrimaryExited); writer.Write(state.PrimaryExitCode); writer.Write(state.InteractiveActive);
            writer.Write(state.InteractiveExited); writer.Write(state.InteractiveExitCode); writer.Write(state.InteractiveId);
            writer.Write(state.Failure ?? ""); writer.Write(state.InteractiveFailure ?? "");
            writer.Write(state.Notice ?? "");
            WriteBytes(writer, state.Buffer); WriteBytes(writer, state.Screen);
        }
        internal static HostState ReadState(BinaryReader reader)
        {
            return new HostState { HostPid = reader.ReadInt32(), HostStartTicks = reader.ReadInt64(), PrimaryPid = reader.ReadInt32(), InteractivePid = reader.ReadInt32(),
                Acknowledged = reader.ReadInt64(), BufferVersion = reader.ReadInt32(), ScreenVersion = reader.ReadInt32(), PrimaryExited = reader.ReadBoolean(), PrimaryExitCode = reader.ReadInt32(),
                InteractiveActive = reader.ReadBoolean(), InteractiveExited = reader.ReadBoolean(), InteractiveExitCode = reader.ReadInt32(), InteractiveId = reader.ReadString(),
                Failure = reader.ReadString(), InteractiveFailure = reader.ReadString(), Notice = reader.ReadString(), Buffer = ReadBytes(reader), Screen = ReadBytes(reader) };
        }
        private static void WriteBytes(BinaryWriter writer, byte[] bytes) { writer.Write(bytes == null ? 0 : bytes.Length); if (bytes != null) writer.Write(bytes); }
        private static byte[] ReadBytes(BinaryReader reader)
        {
            int size = reader.ReadInt32(); if (size < 0 || size > MaxFrame) throw new InvalidDataException("Invalid terminal state size.");
            byte[] bytes = reader.ReadBytes(size); if (bytes.Length != size) throw new EndOfStreamException(); return size == 0 ? null : bytes;
        }
        internal static byte[] Encode(Action<BinaryWriter> write)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new DeflateStream(memory, CompressionLevel.Fastest, true))
                using (var writer = new BinaryWriter(zip, Encoding.UTF8)) write(writer);
                return memory.ToArray();
            }
        }
        internal static void Decode(byte[] data, Action<BinaryReader> read)
        {
            using (var memory = new MemoryStream(data))
            using (var zip = new DeflateStream(memory, CompressionMode.Decompress))
            using (var reader = new BinaryReader(zip, Encoding.UTF8)) read(reader);
        }
    }
}
