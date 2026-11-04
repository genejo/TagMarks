namespace Cauldron.Services;

public sealed class SubstringSearchService
{
    private const int ParallelThreshold = 512;
    
    private readonly List<string> _originalStrings;
    private readonly List<string> _lowerStrings;

    public SubstringSearchService(IEnumerable<string> inputStrings)
    {
        _originalStrings = inputStrings.ToList();
        _lowerStrings = _originalStrings
            .Select(s => s.ToLowerInvariant())
            .ToList();
    }

    /// <summary>
    /// Returns all strings containing every whitespace-delimited term in the query.
    /// Results are unordered; callers must sort if a specific order is needed.
    /// </summary>
    public IEnumerable<string> Query(string searchQuery)
    {
        var terms = searchQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.ToLowerInvariant())
            .ToArray();

        // Early exit
        if (terms.Length == 0)
            return [];

        var results = new List<string>();

        if (_lowerStrings.Count >= ParallelThreshold) {
            var locker = new object();
            Parallel.For(0, _lowerStrings.Count, i =>
            {
                var str = _lowerStrings[i];
                bool isMatch = terms.All(term => str.Contains(term));
                if (!isMatch) return;
                lock (locker)
                    results.Add(_originalStrings[i]);
            });
        }
        else {
            for (int i = 0; i < _lowerStrings.Count; i++) {
                var str = _lowerStrings[i];
                // bool isMatch = terms.All(term => str.Contains(term));
                bool isMatch = true;
                foreach (var term in terms) {
                    if (str.Contains(term)) continue;
                    isMatch = false;
                    break;
                }

                if (isMatch) {
                    results.Add(_originalStrings[i]);
                }
            }
        }

        return results;
    }
}