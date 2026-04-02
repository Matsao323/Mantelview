using System;
using System.Collections.Generic;

namespace Mantelview.Services;

public sealed class TransitionRegistry
{
    private readonly CutTransition _cutTransition = new();
    private readonly List<ITransitionEffect> _effects;
    private readonly List<ITransitionEffect> _nonCutEffects;

    public TransitionRegistry()
    {
        _effects =
        [
            new FadeTransition(),
            _cutTransition,
        ];

        _nonCutEffects = _effects.FindAll(effect => effect is not CutTransition);
    }

    public ITransitionEffect CutEffect => _cutTransition;

    public ITransitionEffect GetEffect(bool isEinkMode)
    {
        if (isEinkMode || _nonCutEffects.Count == 0)
        {
            return _cutTransition;
        }

        return _nonCutEffects[Random.Shared.Next(_nonCutEffects.Count)];
    }
}
