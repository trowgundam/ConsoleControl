using ConsoleControl.Core;
using Tmds.DBus.Protocol;

namespace ConsoleControl.Controller.Bluetooth;

public sealed class BluezControllerOutput : IControllerOutput
{
    private const string BluezDestination = "org.bluez";
    private const string AdapterInterface = "org.bluez.Adapter1";
    private const string DeviceInterface = "org.bluez.Device1";
    private const string CharacteristicInterface = "org.bluez.GattCharacteristic1";
    private const string StateCharacteristicPath = "/service000e/char000f";

    private readonly DBusConnection _connection = new(
        DBusAddress.System ?? throw new PlatformNotSupportedException("The system D-Bus address is unavailable."));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _devicePath;
    private readonly string _characteristicPath;
    private readonly string _adapterPath;
    private bool _dbusConnected;
    private bool _disposed;

    public BluezControllerOutput(string adapterName, string bridgeAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeAddress);

        string normalizedAddress = bridgeAddress.Trim().ToUpperInvariant();
        if (!System.Net.NetworkInformation.PhysicalAddress.TryParse(
                normalizedAddress.Replace(":", string.Empty, StringComparison.Ordinal),
                out System.Net.NetworkInformation.PhysicalAddress? parsed) ||
            parsed.GetAddressBytes().Length != 6)
        {
            throw new ArgumentException("The bridge address must be a six-byte Bluetooth address.", nameof(bridgeAddress));
        }

        _adapterPath = $"/org/bluez/{adapterName}";
        _devicePath = $"{_adapterPath}/dev_{normalizedAddress.Replace(':', '_')}";
        _characteristicPath = _devicePath + StateCharacteristicPath;
    }

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureDbusConnectedAsync(cancellationToken).ConfigureAwait(false);
            bool discoveryStarted = false;
            try
            {
                try
                {
                    await CallWithoutBodyAsync(
                        _adapterPath,
                        AdapterInterface,
                        "StartDiscovery",
                        cancellationToken).ConfigureAwait(false);
                    discoveryStarted = true;
                }
                catch (DBusErrorReplyException exception)
                    when (exception.ErrorName.EndsWith("InProgress", StringComparison.Ordinal))
                {
                    discoveryStarted = true;
                }

                await ConnectDiscoveredDeviceAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (discoveryStarted)
                {
                    try
                    {
                        await CallWithoutBodyAsync(
                            _adapterPath,
                            AdapterInterface,
                            "StopDiscovery",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (DBusErrorReplyException exception)
                        when (exception.ErrorName.EndsWith("NotReady", StringComparison.Ordinal))
                    {
                    }
                }
            }

            byte[] neutral = new byte[ProofBridgeStateEncoder.EncodedLength];
            ProofBridgeStateEncoder.Encode(ControllerState.Neutral, neutral);
            await WriteWhenReadyAsync(neutral, cancellationToken).ConfigureAwait(false);
            IsConnected = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteStateAsync(
        ControllerState state,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] encoded = new byte[ProofBridgeStateEncoder.EncodedLength];
        ProofBridgeStateEncoder.Encode(state, encoded);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("The controller bridge is not connected.");
            }

            await WriteValueAsync(encoded, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            IsConnected = false;
            throw;
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
        IsConnected = false;
        _connection.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureDbusConnectedAsync(CancellationToken cancellationToken)
    {
        if (_dbusConnected)
        {
            return;
        }

        await _connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        _dbusConnected = true;
    }

    private async Task WriteWhenReadyAsync(byte[] state, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (true)
        {
            try
            {
                await WriteValueAsync(state, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DBusErrorReplyException exception)
                when (exception.ErrorName.EndsWith("UnknownObject", StringComparison.Ordinal) &&
                      DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConnectDiscoveredDeviceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                await CallWithoutBodyAsync(
                    _devicePath,
                    DeviceInterface,
                    "Connect",
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DBusErrorReplyException exception)
                when (exception.ErrorName.EndsWith("AlreadyConnected", StringComparison.Ordinal))
            {
                return;
            }
            catch (DBusErrorReplyException exception)
                when (exception.ErrorName.EndsWith("UnknownObject", StringComparison.Ordinal) &&
                      DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
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

        await _connection.CallMethodAsync(message)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteValueAsync(byte[] state, CancellationToken cancellationToken)
    {
        MessageBuffer message;
        using (MessageWriter writer = _connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(
                BluezDestination,
                _characteristicPath,
                CharacteristicInterface,
                "WriteValue",
                "aya{sv}",
                MessageFlags.None);
            writer.WriteArray(state);
            writer.WriteDictionary(
            [
                new KeyValuePair<string, VariantValue>("type", "command"),
            ]);
            message = writer.CreateMessage();
        }

        await _connection.CallMethodAsync(message)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
