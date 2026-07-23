using System.Security.Cryptography;
using System.Text;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

public static class HandshakeProtocol
{
    public static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(32);

    public static string ComputeRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        long timestampUnixMilliseconds,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ValidateNonce(nonceC, nameof(nonceC));
        ValidateNonce(nonceS, nameof(nonceS));
        return ComputeHmac(
            secret,
            BuildTranscript(runtimeInstanceId, timestampUnixMilliseconds, nonceC, nonceS));
    }

    public static string ComputeRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS) =>
        ComputeRuntimeResponse(secret, runtimeInstanceId, 0, nonceC, nonceS);

    public static bool VerifyRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        long timestampUnixMilliseconds,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS,
        string response)
    {
        return VerifyBase64(
            ComputeRuntimeResponse(
                secret,
                runtimeInstanceId,
                timestampUnixMilliseconds,
                nonceC,
                nonceS),
            response);
    }

    public static bool VerifyRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS,
        string response) =>
        VerifyRuntimeResponse(secret, runtimeInstanceId, 0, nonceC, nonceS, response);

    public static string DeriveSessionKey(
        string secret,
        string hostInstanceId,
        string runtimeInstanceId,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        ValidateNonce(nonceC, nameof(nonceC));
        ValidateNonce(nonceS, nameof(nonceS));
        var salt = SHA256.HashData(BuildTranscript(hostInstanceId, 0, nonceC, nonceS));
        var info = Encoding.UTF8.GetBytes($"madorin.ai.runtime.session/{runtimeInstanceId}");
        var ikm = Encoding.UTF8.GetBytes(secret);
        try
        {
            var key = HkdfSha256(ikm, salt, info, 32);
            try
            {
                return Convert.ToBase64String(key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }

    public static string ComputeSessionProof(
        string sessionKey,
        string purpose,
        string hostInstanceId,
        string runtimeInstanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
        var key = Convert.FromBase64String(sessionKey);
        var message = Encoding.UTF8.GetBytes($"{purpose}\0{hostInstanceId}\0{runtimeInstanceId}");
        try
        {
            return Convert.ToBase64String(HMACSHA256.HashData(key, message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static bool VerifySessionProof(
        string sessionKey,
        string purpose,
        string hostInstanceId,
        string runtimeInstanceId,
        string proof)
    {
        return VerifyBase64(
            ComputeSessionProof(sessionKey, purpose, hostInstanceId, runtimeInstanceId),
            proof);
    }

    private static string ComputeHmac(string secret, ReadOnlySpan<byte> input)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(HMACSHA256.HashData(key, input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool VerifyBase64(string expectedBase64, string actualBase64)
    {
        byte[]? expected = null;
        byte[]? actual = null;
        try
        {
            expected = Convert.FromBase64String(expectedBase64);
            actual = Convert.FromBase64String(actualBase64);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (expected is not null)
            {
                CryptographicOperations.ZeroMemory(expected);
            }

            if (actual is not null)
            {
                CryptographicOperations.ZeroMemory(actual);
            }
        }
    }

    private static byte[] BuildTranscript(
        string instanceId,
        long timestampUnixMilliseconds,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS)
    {
        var instanceBytes = Encoding.UTF8.GetBytes(instanceId);
        var timestampBytes = BitConverter.GetBytes(timestampUnixMilliseconds);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(timestampBytes);
        }

        var transcript = new byte[
            instanceBytes.Length + timestampBytes.Length + nonceC.Length + nonceS.Length];
        var offset = 0;
        instanceBytes.CopyTo(transcript, offset);
        offset += instanceBytes.Length;
        timestampBytes.CopyTo(transcript, offset);
        offset += timestampBytes.Length;
        nonceC.CopyTo(transcript.AsSpan(offset));
        offset += nonceC.Length;
        nonceS.CopyTo(transcript.AsSpan(offset));
        return transcript;
    }

    private static void ValidateNonce(ReadOnlySpan<byte> nonce, string parameterName)
    {
        if (nonce.Length != 32)
        {
            throw new ArgumentException("The handshake nonce must contain exactly 32 bytes.", parameterName);
        }
    }

    private static byte[] HkdfSha256(
        ReadOnlySpan<byte> ikm,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info,
        int length)
    {
        var prk = HMACSHA256.HashData(salt, ikm);
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        while (offset < length)
        {
            var input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input.AsSpan(previous.Length));
            input[^1] = counter++;
            var next = HMACSHA256.HashData(prk, input);
            CryptographicOperations.ZeroMemory(previous);
            previous = next;
            var count = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, count).CopyTo(output.AsSpan(offset));
            offset += count;
        }

        CryptographicOperations.ZeroMemory(prk);
        CryptographicOperations.ZeroMemory(previous);
        return output;
    }
}
