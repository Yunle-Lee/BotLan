using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandUI.Controls;

/// <summary>
/// Physics-based spring animation for dependency properties — the same damped
/// harmonic oscillator SwiftUI's spring() uses, integrated per display frame.
/// </summary>
public static class SpringAnimator
{
    private sealed class Spring
    {
        public required IAnimatable Target;
        public required DependencyProperty Property;
        public double Goal;
        public double Velocity;
        public double Stiffness;
        public double Damping;
        public long LastTicks;
        public Action? Completed;
    }

    private static readonly Dictionary<(IAnimatable, DependencyProperty), Spring> _active = [];
    private static bool _listening;

    /// <param name="response">SwiftUI spring "response" — roughly the settle time in seconds.</param>
    /// <param name="dampingFraction">Below 1 overshoots; 0.75–0.86 gives a lively feel.</param>
    public static void Animate(
        IAnimatable target, DependencyProperty property, double goal,
        double response = 0.4, double dampingFraction = 0.8,
        Action? completed = null)
    {
        var key = (target, property);
        var d = (DependencyObject)target;
        double start = d.GetValue(property) is double v ? v : goal;

        double omega = 2 * Math.PI / response;
        var spring = new Spring
        {
            Target = target,
            Property = property,
            Goal = goal,
            Stiffness = omega * omega,
            Damping = 2 * dampingFraction * omega,
            LastTicks = Stopwatch.GetTimestamp(),
            Completed = completed,
        };
        if (_active.TryGetValue(key, out var old))
            spring.Velocity = old.Velocity;

        _active[key] = spring;

        target.BeginAnimation(property, null);
        d.SetValue(property, start);

        if (!_listening)
        {
            _listening = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    public static void Stop(IAnimatable target, DependencyProperty property)
    {
        _active.Remove((target, property));
        target.BeginAnimation(property, null);
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        if (_active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _listening = false;
            return;
        }

        foreach (var (key, spring) in _active.ToArray())
        {
            long now = Stopwatch.GetTimestamp();
            double dt = Math.Min((now - spring.LastTicks) / (double)Stopwatch.Frequency, 0.05);
            spring.LastTicks = now;

            var d = (DependencyObject)spring.Target;
            double value = (double)d.GetValue(spring.Property);

            double accel = -spring.Stiffness * (value - spring.Goal) - spring.Damping * spring.Velocity;
            spring.Velocity += accel * dt;
            value += spring.Velocity * dt;
            d.SetValue(spring.Property, value);

            if (Math.Abs(value - spring.Goal) < 0.01 && Math.Abs(spring.Velocity) < 0.05)
            {
                d.SetValue(spring.Property, spring.Goal);
                _active.Remove(key);
                spring.Completed?.Invoke();
            }
        }
    }
}
