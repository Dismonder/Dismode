namespace Dismode.Core.Transactions;

public enum ExecutionCheckpoint
{
    AfterPreparationJournaled = 1,
    BeforeApply = 2,
    AfterApplyBeforeAppliedJournal = 3,
    AfterAppliedJournaled = 4,
    AfterVerificationBeforeJournal = 5,
    BeforeCompensation = 6,
    AfterCompensationBeforeJournal = 7,
    AfterCompensationJournaled = 8,
}

