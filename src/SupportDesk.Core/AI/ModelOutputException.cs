namespace SupportDesk.Core.AI;

/// <summary>
/// The model answered, but the answer failed the feature's output check (for example an empty summary), so nothing was
/// saved. The message is ours and names the feature only, never the model's output.
/// </summary>
public sealed class ModelOutputException : Exception
{
    public ModelOutputException()
        : this("The model's answer failed the output check.")
    {
    }

    public ModelOutputException(string message)
        : this(message, null)
    {
    }

    public ModelOutputException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
