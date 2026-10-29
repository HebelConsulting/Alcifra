using System.Runtime.ExceptionServices;
using CAManagement.Pkcs11;
using CAManagement.Pkcs11.DataStructures;
using CAManagement.Pkcs11.Extensions;
using Alcifra.ApiClient.KeyTest;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The hardware-key test's way to the key: the one PKCS#11 module this process loads (<see cref="CardModule"/>),
/// inside its gate (ADR 0955, ADR 0832).
/// </summary>
/// <remarks>
/// <para>
/// <b>Failures are carried OUT of the gate, not swallowed in it.</b> Every other card caller answers "unavailable"
/// when something throws, which is right for opening a document (another opener may hold the key) and useless for
/// a test whose whole point is the exact PKCS#11 return value. So each piece of work catches its own exception
/// inside the gate and rethrows it outside.
/// </para>
/// <para>
/// <b>The test logs in afresh, so it drops a held session first.</b> A document read keeps its login for as long as
/// the card is in (<see cref="CardSession"/>); a second login on the same token answers
/// <c>CKR_USER_ALREADY_LOGGED_IN</c>, and reusing the held one would pass the test without proving the PIN. The cost
/// is one PIN prompt at the next document, which a deliberate test is worth.
/// </para>
/// </remarks>
public sealed class Pkcs11KeyPort : IHardwareKeyPort
{
    public async Task<KeyTestLibrary?> FindLibraryAsync()
    {
        if (CardCertificates.FindModule() is not { } path)
        {
            return null;
        }

        return await OnCard(library =>
        {
            var info = library.GetInfo();
            return Task.FromResult<KeyTestLibrary?>(new KeyTestLibrary(
                path,
                info.LibraryVersion.ToString(),
                info.ManufacturerId.AsPkcs11String().Trim(),
                info.LibraryDescription.AsPkcs11String().Trim(),
                info.CryptokiVersion.ToString()));
        });
    }

    public Task<KeyPortSurvey> SurveyAsync() => OnCard(library =>
    {
        var readers = new List<KeyTestReader>();
        foreach (var slot in library.GetSlotList(tokenPresent: false))
        {
            var info = library.GetSlotInfo(slot);
            readers.Add(new KeyTestReader(
                info.SlotDescription.AsPkcs11String().Trim(),
                (info.Flags & CK_SLOT_INFO_FLAGS.CKF_TOKEN_PRESENT) != 0));
        }

        var certificates = CardCertificates.ReadFromLibrary(library);
        var keys = new List<KeyPortKey>();
        var failures = new List<(string, Exception)>();
        foreach (var slot in library.GetSlotList(tokenPresent: true))
        {
            // Per slot, as CardCertificates does: one unreadable key must not hide a good one in the next reader.
            // Inline rather than a helper: a slot id is a CK_ULONG and cannot be named in a signature (ADR 0831).
            string serial = string.Empty;
            try
            {
                var token = library.GetTokenInfo(slot);
                serial = token.SerialNumber.AsPkcs11String().Trim();

                // Public key objects are visible without a login, and say which slots hold a key; private keys stay
                // hidden until the PIN, which this step does not ask for.
                using var session = library.OpenSession(slot, readWrite: false);
                var slots = session.FindObjects(CK_OBJECT_CLASS.CKO_PUBLIC_KEY)
                    .Select(handle =>
                    {
                        try
                        {
                            return session.GetLabel(handle);
                        }
                        catch (Pkcs11Exception)
                        {
                            return string.Empty;   // a key object that will not say its label is still a key
                        }
                    })
                    .Where(label => label.Length > 0)
                    .ToList();

                keys.Add(new KeyPortKey(
                    token.Label.AsPkcs11String().Trim(),
                    token.ManufacturerId.AsPkcs11String().Trim(),
                    token.Model.AsPkcs11String().Trim(),
                    $"{token.FirmwareVersion} (hardware {token.HardwareVersion})",
                    serial,
                    slots,
                    certificates.Where(c => string.Equals(c.TokenSerial, serial, StringComparison.Ordinal)).ToList()));
            }
            catch (Exception e)
            {
                DesktopLog.Warn(e, "The hardware-key test could not read a key; the report says so");
                failures.Add((serial.Length > 0 ? $"serial {serial}" : "a key in a reader", e));
            }
        }

        CardModule.Observed(keys.Count > 0);
        return Task.FromResult(new KeyPortSurvey(readers, keys, failures));
    });

    public Task<byte[]> OpenAsync(KeyPortKey key, CardCertificates.Found certificate, byte[] sealedMessage, string pin) =>
        OnCard(library =>
        {
            CardSession.DropHeld();

            foreach (var slot in library.GetSlotList(tokenPresent: true))
            {
                if (!string.Equals(library.GetTokenInfo(slot).SerialNumber.AsPkcs11String().Trim(), key.SerialNumber, StringComparison.Ordinal))
                {
                    continue;
                }

                Pkcs11Session session;
                LoginScope login;
                try
                {
                    session = library.OpenSession(slot, readWrite: false);
                }
                catch (Exception e)
                {
                    throw new KeyUnlockFailedException(e);
                }

                using (session)
                {
                    try
                    {
                        login = session.Login(pin);
                    }
                    catch (Exception e)
                    {
                        throw new KeyUnlockFailedException(e);
                    }

                    using (login)
                    {
                        return Task.FromResult(CardEnvelopeOpener.OpenOnCard(session, sealedMessage, certificate.KeyId, certificate.Certificate));
                    }
                }
            }

            throw new KeyNotInReaderException(key.SerialNumber);
        });

    /// <summary>Runs <paramref name="work"/> inside the module's gate and rethrows what it threw, outside.</summary>
    private static async Task<T> OnCard<T>(Func<Pkcs11Library, Task<T>> work)
    {
        ExceptionDispatchInfo? failure = null;
        var (done, value) = await CardModule.UseAsync<(bool, T?)>(
            async library =>
            {
                try
                {
                    return (true, await work(library));
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                    return (false, default);
                }
            },
            whenUnavailable: (false, default));

        if (done)
        {
            return value!;
        }

        failure?.Throw();
        throw new KeyLibraryNotLoadedException(CardModule.LastFailure);
    }
}
