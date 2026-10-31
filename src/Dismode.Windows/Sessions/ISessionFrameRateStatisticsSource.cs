namespace Dismode.Windows.Sessions;

internal interface ISessionFrameRateStatisticsSource
{
    void SetSessionStatistics(SessionFrameRateStatisticsAccumulator statistics);
}
