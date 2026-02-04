using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    public class WorkflowQueueCoordinator
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _currentWorkflowType;

        public string? CurrentWorkflowType => _currentWorkflowType;

        public async Task AcquireAsync(string workflowType, CancellationToken ct)
        {
            await _lock.WaitAsync(ct);
            _currentWorkflowType = workflowType;
        }

        public void Release()
        {
            _currentWorkflowType = null;
            _lock.Release();
        }
    }
}
