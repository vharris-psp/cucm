using Vt.ModuleSdk;

return await ModuleApplication
    .Create("vt-cucm", "VT_CUCM")
    .Default("VT_CUCM is ready.")
    .RunAsync(args);
