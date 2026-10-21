namespace SimplArchive.DesktopClient.Services;

/// <summary>A hardware-key test step that could not be done (ADR 0955); the subclass says which.</summary>
/// <remarks>Carries the underlying failure as its inner exception, which is where the report reads the exact code.</remarks>
public abstract class HardwareKeyTestException(string message, Exception? inner) : Exception(message, inner);

/// <summary>The PKCS#11 library was found but could not be loaded or asked about itself.</summary>
public sealed class KeyLibraryNotLoadedException(Exception? inner)
    : HardwareKeyTestException("the PKCS#11 library was found but could not be loaded", inner);

/// <summary>The key was not unlocked: a session could not be opened on it, or the PIN was refused.</summary>
public sealed class KeyUnlockFailedException(Exception inner)
    : HardwareKeyTestException("the key could not be unlocked", inner);

/// <summary>The key whose certificate was read is no longer in a reader.</summary>
public sealed class KeyNotInReaderException(string serial)
    : HardwareKeyTestException($"the key with serial {serial} is no longer in a reader", null);
