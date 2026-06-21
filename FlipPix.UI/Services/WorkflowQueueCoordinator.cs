using System;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Services;

namespace FlipPix.UI.Services
{
    public class WorkflowQueueCoordinator
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ComfyUIService _comfyUIService;
        private string? _currentWorkflowType;

        // Last workflow type that actually held the lease. Used to decide whether the
        // incoming workflow is *switching* models, in which case we free ComfyUI's VRAM
        // first so the next model loads resident instead of inheriting the previous
        // workflow's weights and being forced into slow lowvram streaming.
        private string? _lastWorkflowType;

        public WorkflowQueueCoordinator(ComfyUIService comfyUIService)
            => _comfyUIService = comfyUIService;

        public string? CurrentWorkflowType => _currentWorkflowType;

        public async Task<WorkflowLease> AcquireAsync(string workflowType, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            try
            {
                // Only free when switching to a different workflow than the one that ran
                // last — back-to-back runs of the same tab keep their model warm.
                if (_lastWorkflowType != null && _lastWorkflowType != workflowType)
                {
                    // Best-effort: never let a failed/slow /free block the run.
                    try { await _comfyUIService.FreeMemoryAsync(cancellationToken: ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* ignored — proceed even if free fails */ }
                }

                _currentWorkflowType = workflowType;
                _lastWorkflowType = workflowType;
                return new WorkflowLease(this);
            }
            catch
            {
                _lock.Release();
                throw;
            }
        }

        private void Release()
        {
            _currentWorkflowType = null;
            _lock.Release();
        }

        public sealed class WorkflowLease : IDisposable
        {
            private WorkflowQueueCoordinator? _coordinator;

            internal WorkflowLease(WorkflowQueueCoordinator coordinator)
                => _coordinator = coordinator;

            public void Dispose()
            {
                var coordinator = Interlocked.Exchange(ref _coordinator, null);
                coordinator?.Release();
            }
        }
    }
}
