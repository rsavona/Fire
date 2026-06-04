namespace Fusion.Common.Contracts;

  public interface IElementKey
  {
        string ScopeName { get; init; }
        string DeviceName { get; init; }
        string CoreName { get; init; }
  }
