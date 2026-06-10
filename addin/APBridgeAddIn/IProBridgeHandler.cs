using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn
{
    public interface IProBridgeHandler
    {
        string Op { get; }
        Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken);
    }
}
