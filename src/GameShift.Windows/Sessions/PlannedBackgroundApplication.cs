using GameShift.Contracts.Protocol;
using GameShift.Core.Domain.Identifiers;
using GameShift.Core.Domain.Processes;
using GameShift.Windows.Processes;

namespace GameShift.Windows.Sessions;

public sealed record PlannedBackgroundApplication(
    ActionId ActionId,
    IdempotencyKey IdempotencyKey,
    string DisplayName,
    ProcessIdentity Identity,
    BackgroundProcessActionMode ActionMode,
    ActionId? EcoQosActionId,
    IdempotencyKey? EcoQosIdempotencyKey,
    ActionId? AffinityActionId,
    IdempotencyKey? AffinityIdempotencyKey,
    ActionId? IoPriorityActionId,
    IdempotencyKey? IoPriorityIdempotencyKey,
    ActionId? MemoryPriorityActionId,
    IdempotencyKey? MemoryPriorityIdempotencyKey,
    ApplicationRestartDescriptor? RestartDescriptor,
    long EstimatedWorkingSetBytes,
    // Kiedy pakiet zostal nalozony. Null dla aplikacji z planu — te dostaja
    // pakiet na starcie sesji, wiec poczatek sesji jest wlasciwa granica.
    // Ograniczenia reaktywne nosza wlasna chwile, bo przeglad potomkow
    // liczy sie od niej, nie od startu sesji.
    DateTimeOffset? AppliedAtUtc = null,
    // Czy proces dostal domyslne zbiory procesorow, ktore trzeba wyczyscic
    // przy odtwarzaniu. Tylko ograniczenia reaktywne.
    bool SteeredCpuSets = false);
