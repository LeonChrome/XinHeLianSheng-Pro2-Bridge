using System;
using System.Buffers.Binary;
using System.Threading.Tasks;

namespace Y700Switch2V60Viiper;

// One request, one characteristic owner. A late ACK must not satisfy another
// command or flash address. This is independent of Windows/GATT for replay.
public sealed class Pro2CommandAckWaiter
{
    private readonly object owner;
    private readonly byte[] command;
    private readonly TaskCompletionSource<byte[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Pro2CommandAckWaiter(object owner, ReadOnlySpan<byte> command)
    { this.owner = owner; this.command = command.ToArray(); }
    public Task<byte[]> Task => completion.Task;
    public bool TryComplete(object sender, ReadOnlySpan<byte> response)
    {
        if (!ReferenceEquals(owner, sender) || !BleInitAuditPolicy.AckMatches(command, response)) return false;
        if (command[0] == 0x02 && command[3] == 0x04 && command.Length >= 16 &&
            (response.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(12)) !=
             BinaryPrimitives.ReadUInt32LittleEndian(response[12..]))) return false;
        return completion.TrySetResult(response.ToArray());
    }
}
