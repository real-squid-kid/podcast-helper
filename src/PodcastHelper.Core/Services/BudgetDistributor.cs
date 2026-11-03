using System.Collections.Immutable;
using PodcastHelper.Core.Models;

namespace PodcastHelper.Core.Services;

public sealed record DistributionResult(ImmutableArray<int> Budgets, long CoveredSeconds, long UncoveredSeconds);

public static class BudgetDistributor
{
    public static DistributionResult Distribute(IReadOnlyList<int> budgets, long overrunSeconds, OverrunDistribution strategy)
    {
        ArgumentNullException.ThrowIfNull(budgets);
        if (overrunSeconds < 0 || budgets.Any(x => x < 0) || !Enum.IsDefined(strategy))
            throw new ArgumentException("Budgets and overrun must be non-negative and the strategy must be known.");
        long total = budgets.Sum(x => (long)x);
        long covered = Math.Min(total, overrunSeconds);
        int[] result = budgets.ToArray();
        if (strategy == OverrunDistribution.AdjustNextSegment)
        {
            long left = covered;
            for (int i = 0; i < result.Length && left > 0; i++)
            {
                int cut = (int)Math.Min(result[i], left);
                result[i] -= cut;
                left -= cut;
            }
        }
        else if (total > 0)
        {
            // Int128 защищает произведение суммы секунд и отдельного бюджета от переполнения.
            long cutTotal = 0;
            var remainders = new List<(int Index, Int128 Remainder)>();
            for (int i = 0; i < result.Length; i++)
            {
                Int128 product = (Int128)covered * budgets[i];
                int cut = (int)(product / total);
                result[i] -= cut;
                cutTotal += cut;
                if (result[i] > 0) remainders.Add((i, product % total));
            }
            foreach (var item in remainders.OrderByDescending(x => x.Remainder).ThenBy(x => x.Index).Take((int)(covered - cutTotal)))
                result[item.Index]--;
        }
        return new(result.ToImmutableArray(), covered, overrunSeconds - covered);
    }
}
