using LightHub.Application;

if (args.Length != 1) return 2;
using var lease = new TransactionStore(args[0]).LockBackups();
Console.WriteLine("LOCKED");
Console.ReadLine();
return 0;
