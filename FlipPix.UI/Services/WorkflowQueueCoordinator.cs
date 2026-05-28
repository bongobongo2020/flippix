using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    public class WorkflowQueueCoordinator
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _currentWorkflowType;

        public string? CurrentWorkflowType => _currentWorkflowType;

        public async Task<WorkflowLease> AcquireAsync(string workflowType, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            _currentWorkflowType = workflowType;
            return new WorkflowLease(this);
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
