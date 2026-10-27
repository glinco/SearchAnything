using System;
using System.Collections.Generic;
using System.Linq;

namespace VisionSpike;

/// <summary>
/// Scores predictions against the ground truth the way a user would judge highlights:
/// a highlight is right if it mostly sits on a correct object (or overlaps it well), it is neutral
/// if it sits on something the photo arguably also shows, and anything else is a false alarm.
/// </summary>
internal static class Metrics
{
    public static readonly float[] SiglipThresholds = { 0.001f, 0.002f, 0.005f, 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.3f, 0.5f };
    public static readonly float[] OwlThresholds = { 0.05f, 0.08f, 0.1f, 0.12f, 0.15f, 0.2f, 0.25f, 0.3f, 0.4f, 0.5f };

    private enum Verdict { Hit, Neutral, False }

    private static Verdict Judge(Prediction p, List<GroundTruth> targets, List<GroundTruth> ignored)
    {
        static bool On(Prediction p, GroundTruth g) =>
            Geometry.IoU(p.Box, g.Box) >= 0.25 || Geometry.Intersection(p.Box, g.Box) >= 0.5 * Geometry.Area(p.Box);
        if (targets.Any(g => On(p, g))) return Verdict.Hit;
        if (ignored.Any(g => On(p, g))) return Verdict.Neutral;
        return Verdict.False;
    }

    private static bool Matches(string query, GroundTruth g) => g.Labels.Contains(query);

    private sealed record Row(float Threshold, int Found, int Targets, int[] FoundBySize, int[] TargetsBySize,
                              int Hits, int Falses, int NegativePairs, int NegativePairsWithAlarm);

    public static void Report(List<Scene> scenes, string[] queries, List<Prediction> predictions, float[] thresholds, Action<string> say)
    {
        var bySceneQuery = predictions.GroupBy(p => (p.Scene, p.Query)).ToDictionary(g => g.Key, g => g.ToList());
        List<Prediction> Preds(int s, string q) => bySceneQuery.TryGetValue((s, q), out var l) ? l : new List<Prediction>();

        Row Evaluate(float t)
        {
            int found = 0, targets = 0, hits = 0, falses = 0, negPairs = 0, negAlarm = 0;
            var foundBy = new int[3];
            var targetsBy = new int[3];
            foreach (var scene in scenes)
                foreach (var q in queries)
                {
                    var gts = scene.Objects.Where(g => Matches(q, g)).ToList();
                    var ignored = scene.Objects.Where(g => g.Ignore.Contains(q)).ToList();
                    var preds = Preds(scene.Index, q).Where(p => p.Score >= t).ToList();
                    var verdicts = preds.Select(p => Judge(p, gts, ignored)).ToList();
                    hits += verdicts.Count(v => v == Verdict.Hit);
                    falses += verdicts.Count(v => v == Verdict.False);
                    if (gts.Count == 0)
                    {
                        negPairs++;
                        if (verdicts.Contains(Verdict.False)) negAlarm++;
                    }
                    foreach (var g in gts)
                    {
                        int size = SizeBucket(g);
                        targets++; targetsBy[size]++;
                        if (preds.Any(p => Judge(p, new List<GroundTruth> { g }, new()) == Verdict.Hit)) { found++; foundBy[size]++; }
                    }
                }
            return new Row(t, found, targets, foundBy, targetsBy, hits, falses, negPairs, negAlarm);
        }

        var rows = thresholds.Select(Evaluate).ToList();
        say("");
        say("## Accuracy by threshold");
        say("Recall = targets highlighted. Precision = highlights that are right. Quiet = screens without the thing where nothing is highlighted.");
        say("");
        say("| Threshold | Recall | small <120px | medium | large >350px | Precision | Quiet when absent | F1 |");
        say("|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            double recall = (double)r.Found / r.Targets, precision = r.Hits + r.Falses == 0 ? 1 : (double)r.Hits / (r.Hits + r.Falses);
            say($"| {r.Threshold} | {recall:P0} | {Ratio(r.FoundBySize[0], r.TargetsBySize[0])} | {Ratio(r.FoundBySize[1], r.TargetsBySize[1])} | {Ratio(r.FoundBySize[2], r.TargetsBySize[2])} " +
                $"| {precision:P0} ({r.Hits}/{r.Hits + r.Falses}) | {1 - (double)r.NegativePairsWithAlarm / r.NegativePairs:P0} | {F1(r):F2} |");
        }

        // Threshold-free: can the best score on a screen tell whether the thing is there at all?
        var positives = new List<float>();
        var negatives = new List<float>();
        int topRight = 0;
        foreach (var scene in scenes)
            foreach (var q in queries)
            {
                var gts = scene.Objects.Where(g => Matches(q, g)).ToList();
                var preds = Preds(scene.Index, q);
                float top1 = preds.Count == 0 ? 0 : preds.Max(p => p.Score);
                if (gts.Count > 0)
                {
                    positives.Add(top1);
                    var top = preds.OrderByDescending(p => p.Score).FirstOrDefault();
                    if (top != null && Judge(top, gts, new()) == Verdict.Hit) topRight++;
                }
                else if (!scene.Objects.Any(g => g.Ignore.Contains(q)))
                    negatives.Add(top1);
            }
        double auc = positives.Sum(p => negatives.Count(n => n < p) + 0.5 * negatives.Count(n => n == p)) / ((double)positives.Count * negatives.Count);
        say("");
        say($"Presence AUC (best score on screens that have the thing vs. screens that don't): {auc:F3}");
        say($"Top highlight is right on screens that have the thing: {topRight}/{positives.Count} ({(double)topRight / positives.Count:P0})");

        var best = rows.OrderByDescending(F1).First();
        say("");
        say($"## Per query at threshold {best.Threshold} (best F1)");
        say("| Query | Found | False highlights | Screens with false highlights |");
        say("|---|---|---|---|");
        foreach (var q in queries)
        {
            int found = 0, targets = 0, falses = 0, screensWithFalse = 0;
            foreach (var scene in scenes)
            {
                var gts = scene.Objects.Where(g => Matches(q, g)).ToList();
                var ignored = scene.Objects.Where(g => g.Ignore.Contains(q)).ToList();
                var preds = Preds(scene.Index, q).Where(p => p.Score >= best.Threshold).ToList();
                int f = preds.Count(p => Judge(p, gts, ignored) == Verdict.False);
                falses += f;
                if (f > 0) screensWithFalse++;
                targets += gts.Count;
                found += gts.Count(g => preds.Any(p => Judge(p, new List<GroundTruth> { g }, new()) == Verdict.Hit));
            }
            say($"| {q} | {(targets == 0 ? "(never on screen)" : $"{found}/{targets}")} | {falses} | {screensWithFalse}/{scenes.Count} |");
        }
    }

    private static double F1(Row r)
    {
        double recall = (double)r.Found / r.Targets, precision = r.Hits + r.Falses == 0 ? 1 : (double)r.Hits / (r.Hits + r.Falses);
        return recall + precision == 0 ? 0 : 2 * recall * precision / (recall + precision);
    }

    private static string Ratio(int a, int b) => b == 0 ? "-" : $"{(double)a / b:P0}";

    private static int SizeBucket(GroundTruth g)
    {
        double side = Math.Sqrt(Geometry.Area(g.Box));
        return side < 120 ? 0 : side <= 350 ? 1 : 2;
    }
}
