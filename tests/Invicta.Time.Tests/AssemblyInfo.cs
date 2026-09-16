// © 2026 Andrew Pollard. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;

// Latency assertions are unreliable when tests compete for the thread pool, so never run tests in parallel.
[assembly: Parallelizable(ParallelScope.None)]
