// Every test of ServerOptions.Read sets environment variables, and a process has one environment: two
// tests running at once would read each other's values and pass or fail by timing. The suite is small
// and offline, so serialising it costs nothing worth having.
[assembly: DoNotParallelize]
