namespace VintageHorizons;

/// <summary>
/// Establishes an empty OpenGL error baseline before a self-contained diagnostic operation.
/// OpenGL error flags belong to the current context, so an error left by an earlier caller
/// says nothing about whether the operation being tested is valid. Reading and reporting all
/// incoming flags lets the operation attribute only the errors raised after its baseline.
/// </summary>
internal static class LodGlErrorIsolation
{
    internal const int MaxPreexistingErrors = 16;

    internal static bool TryEstablishBaseline(
        Func<int> readError,
        out string advisory,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(readError);

        var observed = new List<int>();
        for (int i = 0; i <= MaxPreexistingErrors; i++)
        {
            int error = readError();
            if (error == 0)
            {
                advisory = Describe(observed);
                failure = "";
                return true;
            }

            observed.Add(error);
            if (i < MaxPreexistingErrors) continue;

            advisory = Describe(observed);
            failure = $"OpenGL error state did not clear after {MaxPreexistingErrors + 1} reads";
            return false;
        }

        advisory = Describe(observed);
        failure = "OpenGL error baseline could not be established";
        return false;
    }

    static string Describe(IReadOnlyList<int> observed)
    {
        if (observed.Count == 0) return "";
        string names = string.Join(", ", observed.Distinct().Select(DescribeError));
        return $"drained {observed.Count} pre-existing OpenGL error flag(s) ({names})";
    }

    static string DescribeError(int error) => error switch
    {
        0x0500 => "InvalidEnum",
        0x0501 => "InvalidValue",
        0x0502 => "InvalidOperation",
        0x0505 => "OutOfMemory",
        0x0506 => "InvalidFramebufferOperation",
        _ => $"0x{error:X}",
    };
}
