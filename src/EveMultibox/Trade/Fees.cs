namespace EveMultibox.Trade;

/// <summary>
/// NPC market fees. Sources: the Accounting skill's in-game description ("Sales tax starts at
/// 7.5%", "Each level ... reduces sales tax by 11%") and the EVE University wiki for broker fees.
/// Player structures set their own broker fee, so these only apply at NPC stations.
/// </summary>
public static class Fees
{
    public const double BaseSalesTax = 0.075;
    public const double SalesTaxReductionPerLevel = 0.11;

    public const double BaseBrokerFee = 0.03;
    public const double BrokerFeeReductionPerLevel = 0.003;
    public const double FactionStandingFactor = 0.0003;
    public const double CorpStandingFactor = 0.0002;

    /// <summary>Smallest broker fee charged on any order, in ISK.</summary>
    public const double MinBrokerFeeIsk = 100;

    /// <summary>Paid by the seller on every sale.</summary>
    public static double SalesTax(int accountingLevel) =>
        BaseSalesTax * (1 - SalesTaxReductionPerLevel * Clamp(accountingLevel));

    /// <summary>
    /// Paid when placing an order at an NPC station (not when selling straight into a buy order).
    /// Standings are the unmodified values toward the station owner's faction and corporation.
    /// </summary>
    public static double BrokerFee(int brokerRelationsLevel, double factionStanding, double corpStanding) =>
        Math.Max(0, BaseBrokerFee
                    - BrokerFeeReductionPerLevel * Clamp(brokerRelationsLevel)
                    - FactionStandingFactor * factionStanding
                    - CorpStandingFactor * corpStanding);

    public static double BrokerFeeIsk(double orderValue, double rate) => Math.Max(MinBrokerFeeIsk, orderValue * rate);

    private static int Clamp(int level) => Math.Clamp(level, 0, 5);
}
