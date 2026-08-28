namespace ConsoleControl.Core;

public readonly record struct ClientId(Guid Value);

public readonly record struct LeaseGeneration(ulong Value);

public enum ControlPriority
{
    Automation,
    InteractiveUser,
}

public sealed record ControlLease(
    ClientId Owner,
    LeaseGeneration Generation,
    ControlPriority Priority);