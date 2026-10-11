using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Devices.Bluetooth;

namespace Y700Switch2V60Viiper;

public sealed record Pro2ManualCandidate(ulong Address, BluetoothAddressType AddressType,
    string Name, short Rssi, bool NintendoManufacturer, int Score)
{
    public string AddressText => string.Join(":", Enumerable.Range(0, 6)
        .Select(i => ((Address >> ((5 - i) * 8)) & 255).ToString("X2")));
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未提供设备名称" :
        new string(Name.Where(c => !char.IsControl(c)).Take(80).ToArray());
    public string AddressDetail => AddressText + (AddressType == BluetoothAddressType.Random ? " · 随机地址" : " · 公共地址");
    public string Confidence => Score >= 60 ? "高匹配" : Score >= 30 ? "可能是 Pro2" : "低匹配";
    public string Signal => Rssi + " dBm";
    public string Evidence => NintendoManufacturer ? "Nintendo 广播标识" : Score >= 30 ? "设备名称匹配" : "未匹配 Pro2 标识";
    public bool IsValid => Address > 0 && Address <= 0xFFFFFFFFFFFF &&
        AddressType is BluetoothAddressType.Public or BluetoothAddressType.Random;
    public static IReadOnlyList<Pro2ManualCandidate> Sort(IEnumerable<Pro2ManualCandidate> candidates) =>
        candidates.Where(c => c.IsValid).OrderByDescending(c => c.Score).ThenByDescending(c => c.Rssi)
            .ThenBy(c => c.Address).ThenBy(c => c.AddressType).ToArray();
}

public sealed record ManualConnectionResult(bool Connected, string Message);
