namespace Arable;

/// <summary>
/// Something that <b>reads</b> sim state to draw it, refreshed once per
/// rendered frame. A view must never write sim state — see
/// <see cref="ISimSystem"/>.
/// </summary>
public interface ISimView
{
    /// <summary>
    /// Poses the visuals between the previous and current sim state.
    /// <paramref name="alpha"/> is <see cref="SimClock.Alpha"/>, in 0..1.
    /// Because the blend ends at the *current* state, the picture is up to one
    /// tick behind the sim — the price of smoothness, and 50 ms at 20 Hz.
    /// </summary>
    void Interpolate(float alpha);
}
