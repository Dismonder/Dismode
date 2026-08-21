using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameShift.Core.Journal;

namespace GameShift.Data.Journal;

public sealed class AppendOnlyRecoveryJournal : IRecoveryJournal, IDisposable
{
    public const string GenesisHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    private const int MaximumStatePayloadCharacters = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileStream? _writeStream;
    private long _lastSequence;
    private string _lastRecordHash = GenesisHash;
    private bool _tailLoaded;
    private bool _writeFaulted;
    private bool _disposed;

    public AppendOnlyRecoveryJournal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<RecoveryJournalEntry> AppendDurableAsync(
        RecoveryJournalDraft draft,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDraft(draft);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_writeFaulted)
            {
                throw new InvalidOperationException(
                    "The recovery journal writer faulted during a previous "
                    + "durable append. Reopen the journal and run recovery.");
            }

            await EnsureWriterAndTailAsync(cancellationToken)
                .ConfigureAwait(false);

            CanonicalJournalPayload payload = new(
                Sequence: _lastSequence + 1,
                SessionId: draft.SessionId,
                ActionId: draft.ActionId,
                IdempotencyKey: draft.IdempotencyKey,
                EventKind: draft.EventKind,
                TargetKind: draft.TargetKind,
                TargetId: draft.TargetId,
                OriginalStateJson: draft.OriginalStateJson,
                DesiredStateJson: draft.DesiredStateJson,
                CurrentStateJson: draft.CurrentStateJson,
                SessionCheckpoint: draft.SessionCheckpoint,
                Details: draft.Details,
                TimestampUtc: draft.TimestampUtc.ToUniversalTime(),
                PreviousRecordHash: _lastRecordHash);

            string recordHash = ComputeHash(payload);
            RecoveryJournalEntry entry = ToEntry(payload, recordHash);
            byte[] serializedEntry = JsonSerializer.SerializeToUtf8Bytes(
                entry,
                SerializerOptions);
            byte[] line = new byte[serializedEntry.Length + 1];
            serializedEntry.CopyTo(line, 0);
            line[^1] = (byte)'\n';

            try
            {
                await _writeStream!
                    .WriteAsync(line, cancellationToken)
                    .ConfigureAwait(false);
                await _writeStream
                    .FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                _writeStream.Flush(flushToDisk: true);
            }
            catch
            {
                _writeFaulted = true;
                throw;
            }

            _lastSequence = entry.Sequence;
            _lastRecordHash = entry.RecordHash;
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RecoveryJournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _writeStream?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }

    private async ValueTask EnsureWriterAndTailAsync(
        CancellationToken cancellationToken)
    {
        if (_writeStream is null)
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            FileStreamOptions options = new()
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous
                    | FileOptions.WriteThrough,
            };
            _writeStream = new(_path, options);
            _writeStream.Seek(0, SeekOrigin.End);
        }

        if (_tailLoaded)
        {
            return;
        }

        IReadOnlyList<RecoveryJournalEntry> existing =
            await ReadAllCoreAsync(cancellationToken).ConfigureAwait(false);
        RecoveryJournalEntry? previous =
            existing.Count == 0 ? null : existing[^1];
        _lastSequence = previous?.Sequence ?? 0;
        _lastRecordHash = previous?.RecordHash ?? GenesisHash;
        _tailLoaded = true;
        _writeStream.Seek(0, SeekOrigin.End);
    }

    private async ValueTask<IReadOnlyList<RecoveryJournalEntry>> ReadAllCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        FileStreamOptions readOptions = new()
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // A long-running SessionHost keeps the exclusive writer open with
            // FileShare.Read. Independent recovery/update gates must still be
            // able to verify a read-only snapshot without becoming writers.
            Share = FileShare.ReadWrite,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous
                | FileOptions.SequentialScan,
        };
        await using FileStream readStream = new(_path, readOptions);
        if (readStream.Length > int.MaxValue)
        {
            throw new InvalidDataException(
                "The recovery journal is too large to verify.");
        }

        byte[] bytes = new byte[checked((int)readStream.Length)];
        await readStream.ReadExactlyAsync(bytes, cancellationToken)
            .ConfigureAwait(false);

        if (bytes.Length == 0)
        {
            return [];
        }

        if (bytes[^1] != (byte)'\n')
        {
            throw new InvalidDataException(
                "The recovery journal ends with a partial record.");
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The recovery journal is not valid UTF-8.",
                exception);
        }

        string[] lines = content.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<RecoveryJournalEntry> entries = new(lines.Length);
        long expectedSequence = 1;
        string expectedPreviousHash = GenesisHash;

        foreach (string line in lines)
        {
            RecoveryJournalEntry entry;
            try
            {
                entry = JsonSerializer.Deserialize<RecoveryJournalEntry>(
                    line,
                    SerializerOptions)
                    ?? throw new InvalidDataException(
                        "The recovery journal contains a null record.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Recovery journal record {expectedSequence} is invalid JSON.",
                    exception);
            }

            ValidateEntry(entry, expectedSequence, expectedPreviousHash);
            entries.Add(entry);
            expectedSequence++;
            expectedPreviousHash = entry.RecordHash;
        }

        return entries;
    }

    private static void ValidateDraft(RecoveryJournalDraft draft)
    {
        if (draft.SessionId == Guid.Empty)
        {
            throw new ArgumentException("A journal session ID cannot be empty.", nameof(draft));
        }

        bool isSessionCheckpoint =
            draft.EventKind == JournalEventKind.SessionCheckpointRecorded;

        if (isSessionCheckpoint)
        {
            if (draft.ActionId is not null
                || draft.IdempotencyKey is not null
                || draft.SessionCheckpoint is null)
            {
                throw new ArgumentException(
                    "A session checkpoint cannot carry action identifiers and must name a checkpoint.",
                    nameof(draft));
            }
        }
        else
        {
            if (draft.ActionId is null || draft.ActionId.Value == Guid.Empty)
            {
                throw new ArgumentException(
                    "An action journal record requires a non-empty action ID.",
                    nameof(draft));
            }

            if (draft.IdempotencyKey is null
                || draft.IdempotencyKey.Value == Guid.Empty)
            {
                throw new ArgumentException(
                    "An action journal record requires a non-empty idempotency key.",
                    nameof(draft));
            }

            if (draft.SessionCheckpoint is not null)
            {
                throw new ArgumentException(
                    "An action journal record cannot carry a session checkpoint.",
                    nameof(draft));
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(draft.TargetKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.TargetId);
        EnsurePayloadSize(draft.OriginalStateJson, nameof(draft.OriginalStateJson));
        EnsurePayloadSize(draft.DesiredStateJson, nameof(draft.DesiredStateJson));
        EnsurePayloadSize(draft.CurrentStateJson, nameof(draft.CurrentStateJson));
        EnsurePayloadSize(draft.Details, nameof(draft.Details));
    }

    private static void EnsurePayloadSize(string? value, string parameterName)
    {
        if (value?.Length > MaximumStatePayloadCharacters)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value.Length,
                "A journal field cannot exceed one MiB of characters.");
        }
    }

    private static void ValidateEntry(
        RecoveryJournalEntry entry,
        long expectedSequence,
        string expectedPreviousHash)
    {
        if (entry.Sequence != expectedSequence)
        {
            throw new InvalidDataException(
                $"Expected journal sequence {expectedSequence}, found {entry.Sequence}.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(
                entry.PreviousRecordHash,
                expectedPreviousHash))
        {
            throw new InvalidDataException(
                $"Journal hash chain is broken at sequence {entry.Sequence}.");
        }

        CanonicalJournalPayload payload = ToPayload(entry);
        string expectedRecordHash = ComputeHash(payload);

        if (!StringComparer.OrdinalIgnoreCase.Equals(entry.RecordHash, expectedRecordHash))
        {
            throw new InvalidDataException(
                $"Journal record {entry.Sequence} failed its integrity check.");
        }
    }

    private static string ComputeHash(CanonicalJournalPayload payload)
    {
        byte[] canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(canonicalBytes));
    }

    private static RecoveryJournalEntry ToEntry(
        CanonicalJournalPayload payload,
        string recordHash) =>
        new(
            payload.Sequence,
            payload.SessionId,
            payload.ActionId,
            payload.IdempotencyKey,
            payload.EventKind,
            payload.TargetKind,
            payload.TargetId,
            payload.OriginalStateJson,
            payload.DesiredStateJson,
            payload.CurrentStateJson,
            payload.SessionCheckpoint,
            payload.Details,
            payload.TimestampUtc,
            payload.PreviousRecordHash,
            recordHash);

    private static CanonicalJournalPayload ToPayload(RecoveryJournalEntry entry) =>
        new(
            entry.Sequence,
            entry.SessionId,
            entry.ActionId,
            entry.IdempotencyKey,
            entry.EventKind,
            entry.TargetKind,
            entry.TargetId,
            entry.OriginalStateJson,
            entry.DesiredStateJson,
            entry.CurrentStateJson,
            entry.SessionCheckpoint,
            entry.Details,
            entry.TimestampUtc.ToUniversalTime(),
            entry.PreviousRecordHash);

    private sealed record CanonicalJournalPayload(
        long Sequence,
        Guid SessionId,
        Guid? ActionId,
        Guid? IdempotencyKey,
        JournalEventKind EventKind,
        string TargetKind,
        string TargetId,
        string? OriginalStateJson,
        string? DesiredStateJson,
        string? CurrentStateJson,
        SessionCheckpoint? SessionCheckpoint,
        string? Details,
        DateTimeOffset TimestampUtc,
        string PreviousRecordHash);
}
