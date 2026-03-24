namespace CoinBot.Domain.Enums;

public enum ApprovalQueueOperationType
{
    GlobalSystemStateUpdate = 0,
    GlobalPolicyUpdate = 1,
    GlobalPolicyRollback = 2,
    CrisisEscalationExecute = 3
}
