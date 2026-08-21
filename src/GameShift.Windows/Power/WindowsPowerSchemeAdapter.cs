using System.ComponentModel;
using System.Runtime.InteropServices;
using GameShift.Windows.NativeInterop;

namespace GameShift.Windows.Power;

public sealed class WindowsPowerSchemeAdapter : IPowerSchemeAdapter
{
    private const uint MaximumEnumeratedSchemes = 1024;
    private static readonly int GuidSize = Marshal.SizeOf<Guid>();

    public ValueTask<Guid> GetActiveSchemeAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint activeSchemePointer = 0;

        try
        {
            uint result = PowerNativeMethods.PowerGetActiveScheme(
                0,
                out activeSchemePointer);
            ThrowIfFailed(result, "Reading the active power scheme");

            if (activeSchemePointer == 0)
            {
                throw new InvalidDataException(
                    "Windows returned a null active power scheme pointer.");
            }

            Guid activeSchemeId =
                Marshal.PtrToStructure<Guid>(activeSchemePointer);
            if (activeSchemeId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "Windows returned an empty active power scheme identifier.");
            }

            return ValueTask.FromResult(activeSchemeId);
        }
        finally
        {
            if (activeSchemePointer != 0)
            {
                _ = PowerNativeMethods.LocalFree(activeSchemePointer);
            }
        }
    }

    public ValueTask<bool> SchemeExistsAsync(
        Guid schemeId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(schemeId, Guid.Empty);
        nint buffer = Marshal.AllocHGlobal(GuidSize);

        try
        {
            for (uint index = 0; index < MaximumEnumeratedSchemes; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint bufferSize = checked((uint)GuidSize);
                uint result = PowerNativeMethods.PowerEnumerate(
                    0,
                    0,
                    0,
                    PowerNativeMethods.AccessScheme,
                    index,
                    buffer,
                    ref bufferSize);

                if (result == PowerNativeMethods.ErrorNoMoreItems)
                {
                    return ValueTask.FromResult(false);
                }

                ThrowIfFailed(result, "Enumerating power schemes");
                if (bufferSize != GuidSize)
                {
                    throw new InvalidDataException(
                        "Windows returned an invalid power scheme identifier size.");
                }

                Guid currentSchemeId = Marshal.PtrToStructure<Guid>(buffer);
                if (currentSchemeId == schemeId)
                {
                    return ValueTask.FromResult(true);
                }
            }

            throw new InvalidDataException(
                "Power scheme enumeration exceeded the safety limit.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public ValueTask DuplicateSchemeAsync(
        Guid sourceSchemeId,
        Guid destinationSchemeId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(sourceSchemeId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(destinationSchemeId, Guid.Empty);
        cancellationToken.ThrowIfCancellationRequested();

        nint requestedSchemePointer = Marshal.AllocHGlobal(GuidSize);
        nint returnedSchemePointer = requestedSchemePointer;

        try
        {
            Marshal.StructureToPtr(
                destinationSchemeId,
                requestedSchemePointer,
                fDeleteOld: false);
            uint result = PowerNativeMethods.PowerDuplicateScheme(
                0,
                in sourceSchemeId,
                ref returnedSchemePointer);
            ThrowIfFailed(result, "Duplicating the power scheme");

            if (returnedSchemePointer == 0
                || Marshal.PtrToStructure<Guid>(returnedSchemePointer)
                    != destinationSchemeId)
            {
                throw new InvalidDataException(
                    "Windows created a power scheme with an unexpected identifier.");
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            if (returnedSchemePointer != 0
                && returnedSchemePointer != requestedSchemePointer)
            {
                _ = PowerNativeMethods.LocalFree(returnedSchemePointer);
            }

            Marshal.FreeHGlobal(requestedSchemePointer);
        }
    }

    public ValueTask SetActiveSchemeAsync(
        Guid schemeId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(schemeId, Guid.Empty);
        cancellationToken.ThrowIfCancellationRequested();
        uint result = PowerNativeMethods.PowerSetActiveScheme(0, in schemeId);
        ThrowIfFailed(result, "Activating the power scheme");
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteSchemeAsync(
        Guid schemeId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(schemeId, Guid.Empty);
        cancellationToken.ThrowIfCancellationRequested();
        uint result = PowerNativeMethods.PowerDeleteScheme(0, in schemeId);
        ThrowIfFailed(result, "Deleting the power scheme");
        return ValueTask.CompletedTask;
    }

    private static void ThrowIfFailed(uint errorCode, string operation)
    {
        if (errorCode != PowerNativeMethods.ErrorSuccess)
        {
            throw new Win32Exception(
                unchecked((int)errorCode),
                $"{operation} failed with Windows error {errorCode}.");
        }
    }
}
