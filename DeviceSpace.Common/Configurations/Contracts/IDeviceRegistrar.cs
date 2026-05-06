using Microsoft.Extensions.DependencyInjection;

namespace  DeviceSpace.Common.Contracts
{
    /// <summary>
    /// Defines a standard contract for a plug-in to register its
    /// required services with the main application's service container.
    /// </summary>
    public interface IDeviceRegistrar
    {
        /// <summary>
        /// Registers all necessary services for this plug-in.
        /// </summary>
        /// <param name="services">The service collection from the main application.</param>
        void RegisterServices(IServiceCollection services);
    }
}