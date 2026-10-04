using Abeat.Core.Analysis;
using Abeat.Core.Model;

namespace Abeat.Core.Evaluation;

/// <param name="Aligned">Note moments in repeated sections with a note moment at the same position in the
/// section's first occurrence (share of all note moments in repeats).</param>
/// <param name="Same">Share of aligned moments whose notes are identical (hand, cell, cut direction).</param>
public sealed record RepetitionReport(double Aligned, double Same, int Moments);

/// <summary>How much a map repeats itself where the music does: sections with the same label (A, B, ...)
/// are compared beat for beat with the first section of that label.</summary>
public static class RepetitionAnalyzer
{
    /// <param name="toBeat">Map beat to analysis-grid beat (identity for generated maps).</param>
    public static RepetitionReport Analyze(DifficultyMap map, SongAnalysis a, Func<double, double>? toBeat = null)
    {
        toBeat ??= b => b;
        var moments = map.Notes.GroupBy(n => Math.Round(toBeat(n.Beat) * 24))
            .ToDictionary(g => (long)g.Key, g => g.Select(n => (n.Hand, n.X, n.Y, n.Direction)).Order().ToList());
        int total = 0, aligned = 0, same = 0;
        foreach (var group in a.Sections.GroupBy(s => s.Label).Where(g => g.Count() > 1))
        {
            var first = group.First();
            long f0 = (long)Math.Round(a.SecondsToBeat(first.Start)) * 24, fLen = (long)Math.Round(a.SecondsToBeat(first.End)) * 24 - f0;
            foreach (var s in group.Skip(1))
            {
                long s0 = (long)Math.Round(a.SecondsToBeat(s.Start)) * 24, s1 = (long)Math.Round(a.SecondsToBeat(s.End)) * 24;
                foreach (var (k, notes) in moments.Where(m => m.Key >= s0 && m.Key < s1 && m.Key - s0 < fLen))
                {
                    total++;
                    if (!moments.TryGetValue(f0 + (k - s0), out var reference)) continue;
                    aligned++;
                    if (reference.SequenceEqual(notes)) same++;
                }
            }
        }
        return new RepetitionReport(total > 0 ? (double)aligned / total : 0, aligned > 0 ? (double)same / aligned : 0, total);
    }
}
