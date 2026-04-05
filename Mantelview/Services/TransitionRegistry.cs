using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantelview.Services;

public sealed class TransitionRegistry
{
    public const string CutName = "Cut";
    public const string CrossfadeName = "Crossfade";
    public const string SlideName = "Slide";
    public const string SmartSlideName = "SmartSlide";
    public const string CoverName = "Cover";
    public const string UncoverName = "Uncover";

    private static readonly TransitionDirection[] DirectionalTransitions =
    [
        TransitionDirection.Left,
        TransitionDirection.Right,
        TransitionDirection.Up,
        TransitionDirection.Down,
    ];

    private static readonly TransitionDefinition[] Definitions =
    [
        new(CutName, [], [], static _ => new CutTransition()),
        new(CrossfadeName, ["Fade"], [], static _ => new CrossfadeTransition()),
        new(SlideName, ["Push"], DirectionalTransitions, static direction => new SlideTransition(direction)),
        new(SmartSlideName, [], DirectionalTransitions, static direction => new SmartSlideTransition(direction)),
        new(CoverName, [], DirectionalTransitions, static direction => new CoverTransition(direction)),
        new(UncoverName, ["Reveal"], DirectionalTransitions, static direction => new UncoverTransition(direction)),
    ];

    private static readonly Dictionary<string, TransitionDefinition> DefinitionsByConfiguredName = BuildDefinitionsByConfiguredName();
    private static readonly Dictionary<string, TransitionDefinition> DefinitionsByCanonicalName = Definitions.ToDictionary(
        static definition => definition.Name,
        static definition => definition,
        StringComparer.OrdinalIgnoreCase);

    private readonly CutTransition _cutTransition = new();

    public TransitionRegistry()
    {
    }

    public ITransitionEffect CutEffect => _cutTransition;

    public ITransitionEffect GetEffect(bool isEinkMode, IReadOnlyList<string>? allowedEffects)
    {
        if (isEinkMode)
        {
            return _cutTransition;
        }

        var normalizedEffects = NormalizeConfiguredEffects(allowedEffects);
        var selectedDefinition = DefinitionsByCanonicalName[normalizedEffects[Random.Shared.Next(normalizedEffects.Length)]];
        var selectedDirection = selectedDefinition.Directions.Count == 0
            ? TransitionDirection.None
            : selectedDefinition.Directions[Random.Shared.Next(selectedDefinition.Directions.Count)];

        return selectedDefinition.Factory(selectedDirection);
    }

    public static string[] NormalizeConfiguredEffects(IEnumerable<string>? configuredEffects)
    {
        if (configuredEffects is null)
        {
            return [CrossfadeName];
        }

        var normalizedEffects = new List<string>();
        var seenEffects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredEffect in configuredEffects)
        {
            if (string.IsNullOrWhiteSpace(configuredEffect))
            {
                continue;
            }

            if (!DefinitionsByConfiguredName.TryGetValue(configuredEffect.Trim(), out var definition))
            {
                continue;
            }

            if (seenEffects.Add(definition.Name))
            {
                normalizedEffects.Add(definition.Name);
            }
        }

        return normalizedEffects.Count > 0
            ? [.. normalizedEffects]
            : [CrossfadeName];
    }

    private static Dictionary<string, TransitionDefinition> BuildDefinitionsByConfiguredName()
    {
        var map = new Dictionary<string, TransitionDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in Definitions)
        {
            map[definition.Name] = definition;

            foreach (var synonym in definition.Synonyms)
            {
                map[synonym] = definition;
            }
        }

        return map;
    }

    private sealed record TransitionDefinition(
        string Name,
        IReadOnlyList<string> Synonyms,
        IReadOnlyList<TransitionDirection> Directions,
        Func<TransitionDirection, ITransitionEffect> Factory);
}
