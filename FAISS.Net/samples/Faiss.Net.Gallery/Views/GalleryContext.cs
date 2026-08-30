using Faiss.Net.Gallery.Controls;
using Faiss.Net.Gallery.Services;

namespace Faiss.Net.Gallery.Views;

/// <summary>
/// What a demo is handed when it becomes visible: the shared data, the shared probe band at the
/// bottom of the window, and a way to write to the status line.
/// <para>
/// The band belongs to the window rather than to any one demo because it is the app's throughline —
/// whatever you are looking at, it answers the same question: how much of the database did that
/// query actually touch.
/// </para>
/// </summary>
public sealed class GalleryContext(Workspace workspace, ProbeBand band, Action<string> setStatus)
{
    public Workspace Workspace { get; } = workspace;

    public ProbeBand Band { get; } = band;

    /// <summary>Writes the one-line readout beside the band.</summary>
    public void SetStatus(string text) => setStatus(text);
}

/// <summary>A demo in the left rail.</summary>
public interface IGalleryView
{
    /// <summary>Called each time the demo becomes visible. Safe to call more than once.</summary>
    void Activate(GalleryContext context);
}

/// <summary>
/// Implemented by demos that start empty and need a nudge before they are worth photographing — the
/// benchmark table has to be run, the search box has to have something in it. Only the documentation
/// capture path calls this; nothing changes for someone using the app.
/// </summary>
public interface ICapturable
{
    Task PrepareForCaptureAsync();
}
