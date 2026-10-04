// Two reasons, and either would be enough. The options are read out of environment variables, and a
// process has one environment. And what is driven is a program that is a machine-wide singleton, so two
// invocations at once are exactly the arrangement the driver exists to prevent.
[assembly: DoNotParallelize]
