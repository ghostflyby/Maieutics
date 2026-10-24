namespace Maieutics.Control;

/// <summary>Windows named-pipe credential bootstrap surface used by the control host.</summary>
internal interface IWindowsPipeBootstrap
{
    /// <summary>Pipe name REPL children connect to during bootstrap.</summary>
    string PipeName { get; }
}
