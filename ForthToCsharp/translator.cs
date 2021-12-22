using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Linq;

public delegate void Definition(Word w, Translator tr);

public struct Word {
    public string     lastWordName;
    public Definition def;
    public bool       immediate;
    public bool       export;
}

public class Translator {
    // Input and outputs.
    public StringBuilder interpr;
    public StringBuilder compile;
    public TextReader inputReader;

    // State of the interpret.
    public bool Interpreting = true;

    public Translator(TextReader inputReader, StringBuilder interpr, StringBuilder compile) {
        this.interpr = interpr;
        this.compile = compile;
        this.inputReader = inputReader;
    }

    public IEnumerable<string> InputWords() {
        while(true) {
            var line = inputReader.ReadLine();
            if(line == null) yield break; // end of stream

            // The strange empty array is an optimized way to split on system specific whitespace.
            var ss = line.Split(new char[0], StringSplitOptions.RemoveEmptyEntries);
            foreach(var s in ss) yield return s;
        }
    }

    public static bool IsIdentifier(string text)
    {
       if (string.IsNullOrEmpty(text))                return false;
       if (!char.IsLetter(text[0]) && text[0] != '_') return false;

       for (int ix = 1; ix < text.Length; ++ix)
          if (!char.IsLetterOrDigit(text[ix]) && text[ix] != '_')
             return false;

       return true;
    }

    public static string ToCsharpInst(string inst) {
        if(specialInsts.TryGetValue(inst, out var v)) return v;
        if(IsIdentifier(inst))                        return $"VmExt.{inst}(ref vm);";
        return inst;
    }

    public static string ToInstStream(string words) => String.Join(";\n", words.Split(';').Select(ToCsharpInst));

    public static string ToCsharpId(string forthId) {
        StringBuilder sb = new();
        foreach(var c in forthId)
            if(sym.TryGetValue(c, out var v)) sb.Append($"_{v}");
            else sb.Append(c);
        return sb.ToString();
    }
    public static Word inline(string instructions) => new Word {
        lastWordName = "", immediate = false, export = false, def = (word, tr) => {
            var fullInst = $"{{\n{ToInstStream(instructions)}\n}}";
            if(tr.Interpreting) tr.interpr.AppendLine(fullInst);
            else                tr.compile.AppendLine(fullInst);
        }
    };

    public static Word intrinsic(string name) => new Word {
        lastWordName = name, immediate = false, export = true, def = (word, tr) => {
            var csharp = ToCsharpInst(name);
            if(tr.Interpreting) tr.interpr.AppendLine(csharp);
            else                tr.compile.AppendLine(csharp);
        }
    };
    public static void PushNumber(string n, Translator tr) {
        var s = $"VmExt.push(ref vm, {n});";
        if(tr.Interpreting) tr.interpr.AppendLine(s); else tr.compile.AppendLine(s);
    }

    public static void TranslateWord(string word, Translator tr) {
        if(tr.words.TryGetValue(word, out var v)) {
            v.def(v, tr);
        } else if(nint.TryParse(word, out var _)) {
            PushNumber(word, tr);
        } else
        throw new Exception($"{word} is not in the dictionary");
    }
    public static void Translate(Translator tr) {
        foreach(var w in tr.InputWords()) TranslateWord(w, tr);
    }
    public static (string,string) TranslateString(string forthCode) {
        var isb = new StringBuilder();
        var csb = new StringBuilder();
        var tr = new Translator(new StringReader(forthCode), isb, csb);
        Translate(tr);
        return (tr.interpr.ToString(), tr.compile.ToString());
    }

    public static string ToCSharp(string funcName, string interpret, string compile) =>
        $@"
        static public partial class __GEN {{
            static public long Test{funcName}() {{
                var vm = new Vm(System.Console.In, System.Console.Out);
                {funcName}(ref vm);
                var res = VmExt.pop(ref vm);
                VmExt.depth(ref vm);
                var zero = VmExt.pop(ref vm);
                return res + zero;
            }}
            {compile}
            static public void {funcName} (ref Vm vm) {{
                {interpret}
            }}
        }}
        ";

    // The Forth dictionary.
    public Dictionary<string, Word> words = new() {
            {"+"       ,  inline("popa;popb;var c = a + b;pushc;") },
            {"dup"     ,  intrinsic("dup")},
            {"dup2"    ,  intrinsic("dup2")},
            {"drop"    ,  intrinsic("drop")},
            {"drop2"   ,  intrinsic("drop2")},
            {"cells"   ,  intrinsic("cells")},
            {"here"    ,  intrinsic("here")},
            {"@"       ,  intrinsic("_fetch")},
            {"c@"      ,  intrinsic("_cfetch")},
            {"!"       ,  intrinsic("_store")},
            {"c!"      ,  intrinsic("_cstore")},
            {","       ,  intrinsic("_comma")},
            {"c,"      ,  intrinsic("_ccomma")},
            {"allot"   ,  intrinsic("allot")},
            {"align"   ,  intrinsic("align")},
            {"aligned" ,  intrinsic("aligned")},
            {"type"    ,  intrinsic("type")},
            {"source"  ,  intrinsic("source")},
            {"count"   ,  intrinsic("count")},
            {"refill"  ,  intrinsic("refill")},
            {"word"    ,  intrinsic("word")},
            {"bl"      ,  intrinsic("bl")},
        };

    // Maps symbols to words
    public static Dictionary<char, string> sym = new() {
        {'+', "plus"},
        {'-', "minus"}
    };

    public static Dictionary<string, string> specialInsts = new() {
        {"popa", "var a = VmExt.pop(ref vm)"},
        {"popb", "var b = VmExt.pop(ref vm)"},
        {"popc", "var c = VmExt.pop(ref vm)"},

        {"cpopa", "var aa = VmExt.cpop(ref vm)"},
        {"cpopb", "var bb = VmExt.cpop(ref vm)"},
        {"cpopc", "var cc = VmExt.cpop(ref vm)"},

        {"pusha", "VmExt.push(ref vm, a)"},
        {"pushb", "VmExt.push(ref vm, b)"},
        {"pushc", "VmExt.push(ref vm, c)"},

        {"cpusha", "VmExt.cpush(ref vm, ca)"},
        {"cpushb", "VmExt.cpush(ref vm, cb)"},
        {"cpushc", "VmExt.cpush(ref vm, cc)"},
    };
}
