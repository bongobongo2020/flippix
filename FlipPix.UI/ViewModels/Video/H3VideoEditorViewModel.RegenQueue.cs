using System.Collections.Generic;
using System.Linq;
using FlipPix.UI.Models;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ↻ while something is already generating: the clip joins a queue instead of being ignored, shows
    /// "⏳ QUEUED #n" on the strip, and is regenerated as soon as the editor is free, in the order clicked.
    /// ↻ on a queued clip takes it back out; Cancel empties the queue along with the running job.
    ///
    /// <para>What ↻ does is decided when the clip's turn comes (preset, 2-Pass new take vs. same seed), so a
    /// preset or prompt changed while it waits is the one used.</para>
    /// </summary>
    public partial class H3VideoEditorViewModel
    {
        private readonly List<H3TimelineClip> _regenQueue = new();

        /// <summary>"↻ 2 queued" beside the clip count; empty when nothing waits.</summary>
        public string RegenQueueNote => _regenQueue.Count == 0 ? string.Empty : $"↻ {_regenQueue.Count} queued";

        private void RequestRegenerate(H3TimelineClip? clip)
        {
            if (clip == null) return;
            if (!IsGenerating)
            {
                _ = RegenerateClipAsync(clip);
                return;
            }

            IsLogOpen = true;
            if (clip.IsRendering)
            {
                AddLog($"Clip {clip.DisplayIndex} is generating right now.");
                return;
            }
            if (clip.IsQueued)
            {
                AddLog($"Clip {clip.DisplayIndex} is already waiting in this Generate run.");
                return;
            }

            if (_regenQueue.Remove(clip))
                AddLog($"↻ Clip {clip.DisplayIndex} taken out of the queue.");
            else
            {
                _regenQueue.Add(clip);
                AddLog($"↻ Clip {clip.DisplayIndex} queued (#{_regenQueue.Count}) — it regenerates when the current job finishes.");
            }
            RenumberRegenQueue();
        }

        /// <summary>Starts the next queued clip once the editor is free. Posted, so the job that just finished
        /// completes its own clean-up first, and anything that starts generating in the meantime goes first.</summary>
        private void DrainRegenQueueSoon()
        {
            if (_regenQueue.Count == 0) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) StartNextQueuedRegen();
            else dispatcher.BeginInvoke(StartNextQueuedRegen);
        }

        private void StartNextQueuedRegen()
        {
            if (IsGenerating) return; // picked up again when that job ends
            while (_regenQueue.Count > 0)
            {
                var clip = _regenQueue[0];
                _regenQueue.RemoveAt(0);
                if (!TimelineClips.Contains(clip)) continue; // removed while it waited
                RenumberRegenQueue();
                clip.RegenQueuePosition = 0;
                _ = RegenerateClipAsync(clip);
                return;
            }
            RenumberRegenQueue();
        }

        private void DropFromRegenQueue(H3TimelineClip clip)
        {
            if (!_regenQueue.Remove(clip)) return;
            clip.RegenQueuePosition = 0;
            RenumberRegenQueue();
        }

        private void ClearRegenQueue()
        {
            if (_regenQueue.Count == 0) return;
            AddLog($"↻ {_regenQueue.Count} queued clip(s) dropped.");
            foreach (var clip in _regenQueue) clip.RegenQueuePosition = 0;
            _regenQueue.Clear();
            RenumberRegenQueue();
        }

        private void RenumberRegenQueue()
        {
            for (int i = 0; i < _regenQueue.Count; i++) _regenQueue[i].RegenQueuePosition = i + 1;
            foreach (var clip in TimelineClips.Where(c => c.IsRegenQueued && !_regenQueue.Contains(c)))
                clip.RegenQueuePosition = 0;
            OnPropertyChanged(nameof(RegenQueueNote));
        }
    }
}
