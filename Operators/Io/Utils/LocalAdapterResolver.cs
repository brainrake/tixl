#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Io.Utils;

/// <summary>Resolves a saved IPv4 address to an exact local address or a unique subnet match.</summary>
internal sealed class LocalAdapterResolver(Func<LocalAdapterResolver.Adapter[]> enumerateAdapters)
{
    internal sealed class Adapter(IPAddress address, IPAddress subnetMask, string name)
    {
        internal IPAddress Address { get; } = address;
        internal IPAddress SubnetMask { get; } = subnetMask;
        internal string DisplayName { get; } = $"{name}: {address}";
    }

    internal LocalAdapterResolver() : this(EnumerateAdapters) { }

    internal IPAddress? EffectiveAddress { get; private set; }
    internal IPAddress? SubnetMask { get; private set; }
    internal string? ErrorMessage { get; private set; }
    internal string DisplayValue { get; private set; } = string.Empty;
    internal string SendingMessage { get; private set; } = string.Empty;
    internal string DisabledMessage { get; private set; } = string.Empty;

    internal bool Update(string? configuredAddress, string? targetAddress, bool forceRefresh = false)
    {
        configuredAddress ??= string.Empty;
        targetAddress ??= string.Empty;
        if (!forceRefresh && _configuredAddress == configuredAddress && _targetAddress == targetAddress)
            return false;

        Refresh();
        _configuredAddress = configuredAddress;
        _targetAddress = targetAddress;
        var previousAddress = EffectiveAddress;
        var previousMask = SubnetMask;
        EffectiveAddress = null;
        SubnetMask = null;
        ErrorMessage = null;
        Resolve(configuredAddress, targetAddress);

        var effectiveAddress = EffectiveAddress?.ToString();
        bool remapped = effectiveAddress != null && effectiveAddress != configuredAddress;
        DisplayValue = remapped ? $"{configuredAddress} (effective: {effectiveAddress})" : configuredAddress;
        var addressDescription = remapped ? $"Saved {configuredAddress}; effective {effectiveAddress}." : $"Local IP {effectiveAddress}.";
        SendingMessage = $"Connected and sending. {addressDescription}";
        DisabledMessage = $"Sending is disabled. Enable 'Send Trigger'. {addressDescription}";
        return !Equals(previousAddress, EffectiveAddress) || !Equals(previousMask, SubnetMask);
    }

    internal IEnumerable<string> GetOptions()
    {
        Refresh();
        foreach (var adapter in _adapters)
            yield return adapter.DisplayName;
    }

    internal Adapter? GetAdapter(string displayName)
    {
        foreach (var adapter in _adapters)
        {
            if (adapter.DisplayName == displayName)
                return adapter;
        }
        return null;
    }

    private void Refresh()
    {
        // A failed refresh must not reuse a stale adapter list.
        try { _adapters = enumerateAdapters(); }
        catch (NetworkInformationException) { _adapters = Array.Empty<Adapter>(); }
        catch (SocketException) { _adapters = Array.Empty<Adapter>(); }
    }

    private void Resolve(string configuredAddress, string targetAddress)
    {
        if (string.IsNullOrWhiteSpace(configuredAddress))
        {
            ErrorMessage = "Select a Local IP Address to send from.";
        }
        else if (!IPAddress.TryParse(configuredAddress, out var configuredIp) || configuredIp.AddressFamily != AddressFamily.InterNetwork)
        {
            ErrorMessage = $"Local IP '{configuredAddress}' is not IPv4. Select a local adapter.";
        }
        else if (configuredIp.Equals(IPAddress.Any))
        {
            EffectiveAddress = IPAddress.Any;
        }
        else if (IPAddress.IsLoopback(configuredIp))
        {
            EffectiveAddress = configuredIp;
            SubnetMask = LoopbackMask;
        }
        else
        {
            ResolveAdapter(configuredIp, targetAddress);
        }
    }

    private void ResolveAdapter(IPAddress configuredIp, string targetAddress)
    {
        foreach (var adapter in _adapters)
        {
            if (!adapter.Address.Equals(configuredIp))
                continue;
            Select(adapter);
            return;
        }

        int count = FindSubnetMatches(configuredIp, out var match);
        if (count == 0 && IPAddress.TryParse(targetAddress, out var targetIp) && targetIp.AddressFamily == AddressFamily.InterNetwork)
            count = FindSubnetMatches(targetIp, out match);

        if (count == 1)
        {
            Select(match!);
            return;
        }

        ErrorMessage = count > 1
                           ? $"Multiple local addresses match {configuredIp}. Select a local address."
                           : $"No local address matches {configuredIp}. Select a local adapter.";
    }

    private int FindSubnetMatches(IPAddress address, out Adapter? match)
    {
        match = null;
        if (!IsUnicast(address) || IPAddress.IsLoopback(address))
            return 0;

        int count = 0;
        uint remote = ToUInt32(address);
        foreach (var adapter in _adapters)
        {
            if (!IsUnicast(adapter.Address) || IPAddress.IsLoopback(adapter.Address))
                continue;
            uint mask = ToUInt32(adapter.SubnetMask);
            uint hostMask = ~mask;
            if (mask == 0 || (hostMask & (hostMask + 1)) != 0)
                continue;
            uint local = ToUInt32(adapter.Address);
            if ((local & mask) != (remote & mask))
                continue;
            // Network and broadcast destinations do not identify a meaningful target subnet.
            if (hostMask > 1 && ((remote & hostMask) == 0 || (remote & hostMask) == hostMask))
                continue;
            match = adapter;
            count++;
        }
        return count;
    }

    private void Select(Adapter adapter)
    {
        EffectiveAddress = adapter.Address;
        SubnetMask = adapter.SubnetMask;
    }

    private static bool IsUnicast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        uint value = ToUInt32(address);
        return (value >> 24) != 0 && (value >> 24) < 224;
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static Adapter[] EnumerateAdapters()
    {
        var adapters = new List<Adapter> { new(IPAddress.Loopback, LoopbackMask, "Localhost") };
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            try
            {
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && IsUnicast(unicast.Address))
                        adapters.Add(new Adapter(unicast.Address, unicast.IPv4Mask, networkInterface.Name));
                }
            }
            catch (NetworkInformationException) { /* The adapter can disappear during enumeration. */ }
        }
        return adapters.ToArray();
    }

    private static readonly IPAddress LoopbackMask = IPAddress.Parse("255.0.0.0");
    private Adapter[] _adapters = Array.Empty<Adapter>();
    private string? _configuredAddress;
    private string? _targetAddress;
}
