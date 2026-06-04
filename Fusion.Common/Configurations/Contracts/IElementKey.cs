namespace Fusion.Common.Contracts;

  public interface IElementKey
  {
        string ScopeName { get; init; }
        string ElementName { get; init; }
        string CoreName { get; init; }
  }
