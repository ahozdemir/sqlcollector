---
description: Run tests via the test-runner subagent (Sonnet 5.5). Usage /test [unit|integration|all|smoke|<dotnet test filter>]
---
Use the `test-runner` subagent to run: $ARGUMENTS

If no argument was given, run "all" (`dotnet test SqlCollector.sln`). Relay its verdict and failures to me in Turkish,
then propose (but do not yet apply) fixes for any failure.
