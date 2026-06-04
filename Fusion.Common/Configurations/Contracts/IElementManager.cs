
using Fusion.Common.Contracts;

namespace Fusion.Common.Contracts
{
    public interface IElementManager
    {
        // Identification
        public Task<bool> TakeElementOfflineAsync(string elementName);
        public Task ReinitializeElementAsync(string elementName);

        /// <summary>
        /// Returns the name of the test counterpart for this manager, if defined via TestCounterpartAttribute.
        /// </summary>
        string? TestCounterpart { get; }
    }
}

public interface IElementManagerFactory
{
    IElementManager CreateElementManager(string managerType);
}