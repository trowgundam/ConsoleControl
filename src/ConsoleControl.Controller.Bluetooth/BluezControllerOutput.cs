using System.Collections.Immutable;
using System.Net.NetworkInformation;

using ConsoleControl.Core;

using Tmds.DBus.Protocol;

namespace ConsoleControl.Controller.Bluetooth;

public sealed class BluezControllerOutput : IControllerBridgeAdapter
{
    public const string BridgeServiceUuid = "cc7c0001-8f5d-4b8a-9f6e-4f5f4343544c";
    public const string StateCharacteristicUuid = "cc7c0002-8f5d-4b8a-9f6e-4f5f4343544c";

    private const string BluezDestination = "org.bluez";
    private const string AdapterInterface = "org.bluez.Adapter1";
    private const string DeviceInterface = "org.bluez.Device1";
    private const string CharacteristicInterface = "org.bluez.GattCharacteristic1";
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";

    private readonly DBusConnection _connection = new(
        DBusAddress.System ?? throw new PlatformNotSupportedException("The system D-Bus address is unavailable."));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _adapterPath;
    private BridgeSession? _session;
    private bool _dbusConnected;
    private bool _disposed;

    public BluezControllerOutput(string adapterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        _adapterPath = $"/org/bluez/{adapterName}";
    }

    public ControllerBridgeId? SelectedBridgeId { get; private set; }

    public bool IsConnected => _session?.IsConnected == true;

    public async Task<ImmutableArray<ControllerBridge>> EnumerateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureDbusConnectedAsync(cancellationToken).ConfigureAwait(false);
            await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            return (await GetManagedObjectsAsync(cancellationToken).ConfigureAwait(false))
                .Where(static item => item.Value.TryGetValue(DeviceInterface, out _))
                .Where(item => item.Key.StartsWith(_adapterPath + "/dev_", StringComparison.Ordinal))
                .Select(item => CreateBridge(item.Value[DeviceInterface]))
                .Where(static bridge => bridge is not null)
                .Select(static bridge => bridge!)
                .OrderBy(static bridge => bridge.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static bridge => bridge.Id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ConfigureAsync(ControllerBridgeId? bridgeId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bridgeId is { } selected)
        {
            ValidateAddress(selected.Value);
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SelectedBridgeId = bridgeId;
            _session = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (SelectedBridgeId is not { } selected)
            {
                throw new InvalidOperationException("No controller bridge is selected.");
            }

            await EnsureDbusConnectedAsync(cancellationToken).ConfigureAwait(false);
            string devicePath = DevicePath(selected);
            await ConnectDeviceAsync(devicePath, cancellationToken).ConfigureAwait(false);
            string characteristicPath = await ResolveStateCharacteristicAsync(devicePath, cancellationToken)
                .ConfigureAwait(false);
            BridgeSession session = new(characteristicPath);
            await WriteValueAsync(session.CharacteristicPath, Encode(ControllerState.Neutral), cancellationToken)
                .ConfigureAwait(false);
            session.IsConnected = true;
            _session = session;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteStateAsync(ControllerState state, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not { IsConnected: true } session)
            {
                throw new InvalidOperationException("The controller bridge is not connected.");
            }

            try
            {
                await WriteValueAsync(session.CharacteristicPath, Encode(state), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                session.IsConnected = false;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _session = null;
        _connection.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task DiscoverAsync(CancellationToken cancellationToken)
    {
        bool discoveryRunning = false;
        try
        {
            try
            {
                await CallWithoutBodyAsync(_adapterPath, AdapterInterface, "StartDiscovery", cancellationToken)
                    .ConfigureAwait(false);
                discoveryRunning = true;
            }
            catch (DBusErrorReplyException exception)
                when (exception.ErrorName.EndsWith("InProgress", StringComparison.Ordinal))
            {
                discoveryRunning = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (discoveryRunning)
            {
                using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(2));
                try
                {
                    await CallWithoutBodyAsync(
                        _adapterPath,
                        AdapterInterface,
                        "StopDiscovery",
                        cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch (DBusErrorReplyException exception)
                    when (exception.ErrorName.EndsWith("NotReady", StringComparison.Ordinal))
                {
                }
                catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task ConnectDeviceAsync(string devicePath, CancellationToken cancellationToken)
    {
        try
        {
            await CallWithoutBodyAsync(devicePath, DeviceInterface, "Connect", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DBusErrorReplyException exception)
            when (exception.ErrorName.EndsWith("AlreadyConnected", StringComparison.Ordinal))
        {
        }
    }

    private async Task<string> ResolveStateCharacteristicAsync(
        string devicePath,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (true)
        {
            Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> objects =
                await GetManagedObjectsAsync(cancellationToken).ConfigureAwait(false);
            foreach ((string path, Dictionary<string, Dictionary<string, VariantValue>> interfaces) in objects)
            {
                if (path.StartsWith(devicePath + "/", StringComparison.Ordinal) &&
                    interfaces.TryGetValue(CharacteristicInterface, out Dictionary<string, VariantValue>? properties) &&
                    TryGetString(properties, "UUID", out string uuid) &&
                    string.Equals(uuid, StateCharacteristicUuid, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"The selected device does not expose the ConsoleControl state characteristic {StateCharacteristicUuid}.");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>>
        GetManagedObjectsAsync(CancellationToken cancellationToken)
    {
        MessageBuffer message;
        using (MessageWriter writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                BluezDestination,
                "/",
                ObjectManagerInterface,
                "GetManagedObjects",
                signature: null,
                MessageFlags.None);
            message = writer.CreateMessage();
        }

        return await _connection.CallMethodAsync<Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>>(
            message,
            static (reply, _) => ReadManagedObjects(reply.GetBodyReader()),
            null).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> ReadManagedObjects(
        Reader reader)
    {
        Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>> objects = [];
        ArrayEnd objectsEnd = reader.ReadDictionaryStart();
        while (reader.HasNext(objectsEnd))
        {
            string path = reader.ReadObjectPathAsString();
            Dictionary<string, Dictionary<string, VariantValue>> interfaces = [];
            ArrayEnd interfacesEnd = reader.ReadDictionaryStart();
            while (reader.HasNext(interfacesEnd))
            {
                string interfaceName = reader.ReadString();
                interfaces[interfaceName] = reader.ReadDictionaryOfStringToVariantValue();
            }

            objects[path] = interfaces;
        }

        return objects;
    }

    private static ControllerBridge? CreateBridge(Dictionary<string, VariantValue> properties)
    {
        if (!TryGetStrings(properties, "UUIDs", out string[] uuids) ||
            !uuids.Contains(BridgeServiceUuid, StringComparer.OrdinalIgnoreCase) ||
            !TryGetString(properties, "Address", out string address))
        {
            return null;
        }

        string displayName = TryGetString(properties, "Alias", out string alias) ? alias :
            TryGetString(properties, "Name", out string name) ? name : address;
        bool connected = properties.TryGetValue("Connected", out VariantValue value) &&
            value.GetBool();
        return new(new(address.ToUpperInvariant()), displayName, connected);
    }

    private static bool TryGetString(
        Dictionary<string, VariantValue> properties,
        string name,
        out string value)
    {
        if (properties.TryGetValue(name, out VariantValue variant))
        {
            value = variant.GetString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetStrings(
        Dictionary<string, VariantValue> properties,
        string name,
        out string[] value)
    {
        if (properties.TryGetValue(name, out VariantValue variant))
        {
            value = variant.GetArray<string>();
            return true;
        }

        value = [];
        return false;
    }

    private static void ValidateAddress(string address)
    {
        if (!PhysicalAddress.TryParse(address.Replace(":", string.Empty, StringComparison.Ordinal), out PhysicalAddress? parsed) ||
            parsed.GetAddressBytes().Length != 6)
        {
            throw new ArgumentException("The bridge ID is not a six-byte Bluetooth address.", nameof(address));
        }
    }

    private string DevicePath(ControllerBridgeId bridgeId) =>
        $"{_adapterPath}/dev_{bridgeId.Value.ToUpperInvariant().Replace(':', '_')}";

    private static byte[] Encode(ControllerState state)
    {
        byte[] encoded = new byte[ProofBridgeStateEncoder.EncodedLength];
        ProofBridgeStateEncoder.Encode(state, encoded);
        return encoded;
    }

    private async Task EnsureDbusConnectedAsync(CancellationToken cancellationToken)
    {
        if (!_dbusConnected)
        {
            await _connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            _dbusConnected = true;
        }
    }

    private async Task CallWithoutBodyAsync(
        string path,
        string interfaceName,
        string member,
        CancellationToken cancellationToken)
    {
        MessageBuffer message;
        using (MessageWriter writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                BluezDestination,
                path,
                interfaceName,
                member,
                signature: null,
                MessageFlags.None);
            message = writer.CreateMessage();
        }

        await _connection.CallMethodAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteValueAsync(
        string characteristicPath,
        byte[] state,
        CancellationToken cancellationToken)
    {
        MessageBuffer message;
        using (MessageWriter writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                BluezDestination,
                characteristicPath,
                CharacteristicInterface,
                "WriteValue",
                "aya{sv}",
                MessageFlags.None);
            writer.WriteArray(state);
            writer.WriteDictionary([new KeyValuePair<string, VariantValue>("type", "command")]);
            message = writer.CreateMessage();
        }

        await _connection.CallMethodAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record BridgeSession(string CharacteristicPath)
    {
        public bool IsConnected { get; set; }
    }
}