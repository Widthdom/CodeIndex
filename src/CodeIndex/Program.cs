using System.Text;
using CodeIndex.Cli;

// On Windows the console defaults to the OEM code page, causing Unicode
// characters (box-drawing, block elements, etc.) to appear as '?'.
// Windows のコンソールは既定で OEM コードページを使用するため、Unicode 文字が文字化けします。
Console.OutputEncoding = Encoding.UTF8;
ConsoleUi.EnsureConsoleWritersSynchronized();
return ProgramRunner.Run(args);
