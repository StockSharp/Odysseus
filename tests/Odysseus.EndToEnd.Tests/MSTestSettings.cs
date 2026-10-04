// Every test here starts a real Odysseus.Server process and speaks the protocol to it. Running them
// against each other multiplies those processes, and the suite then competes with the fourteen other
// test assemblies dotnet test runs in parallel - which is how three of them began timing out on a
// loaded machine while passing on an idle one. The cost of holding them in line is a couple of minutes.
[assembly: DoNotParallelize]
