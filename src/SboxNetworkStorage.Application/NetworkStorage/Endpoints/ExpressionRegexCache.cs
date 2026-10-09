using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Runs patterns that are only known at run time. Compiled patterns are kept in a bounded least-recently-used
/// cache, and every match has a hard timeout: a pattern that runs away ends in an <see cref="ExpressionException"/>,
/// the same error every other expression failure produces, instead of holding the request thread.
/// </summary>
public sealed class ExpressionRegexCache(int capacity, TimeSpan timeout)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Pattern, Regex Regex)>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Pattern, Regex Regex)> _recent = new();

    /// <summary>Number of compiled patterns currently cached; never above the capacity.</summary>
    public int Count
    {
        get { lock (_gate) return _index.Count; }
    }

    public Match Match(string pattern, string input)
    {
        try
        {
            var regex = Get(pattern);
            return regex.Match(input);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ExpressionException($"Pattern matching exceeded {timeout.TotalMilliseconds:0} ms and was stopped.");
        }
        catch (ArgumentException ex)
        {
            throw new ExpressionException($"Invalid pattern: {ex.Message}");
        }
    }

    private Regex Get(string pattern)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(pattern, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value.Regex;
            }
        }

        // Construct outside the lock; two threads racing on a new pattern just build it twice.
        var regex = new Regex(pattern, RegexOptions.Compiled, timeout);
        lock (_gate)
        {
            if (_index.TryGetValue(pattern, out var existing))
            {
                _recent.Remove(existing);
                _recent.AddFirst(existing);
                return existing.Value.Regex;
            }
            _index[pattern] = _recent.AddFirst((pattern, regex));
            while (_index.Count > capacity)
            {
                _index.Remove(_recent.Last!.Value.Pattern);
                _recent.RemoveLast();
            }
        }
        return regex;
    }
}
