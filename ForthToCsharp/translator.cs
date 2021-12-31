#define NOBASEOFF
#define NOFASTCONSTANT
using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Linq;

public delegate void Definition(Word w, Translator tr);

public struct Word {
    public Definition def;
    public bool       immediate;
    public bool       export;
    public bool       tickdefined;
    public string     name;
}

public class Translator {

    // Input and outputs.
    public StringBuilder output;
    public TextReader inputReader;

    public string? line;

    // Manage definition words.
    public bool InDefinition  = false;
    public string lastWord    = "";
    public string lastCreated = "";

    List<Word>? defActions;
    List<Word>? doesActions;
    List<Word>? actions;

    // Iteration count for for loops.
    public int nested = 0;

    // Create unique names for goto statements.
    public int nameCount = 0;

    public Translator(TextReader inputReader, StringBuilder output) {
        this.output  = output;
        this.inputReader = inputReader;
    }

    // I can't use an iterator returning method because CreateWord need access to the next word.
    public string? NextWord(char sep = ' ') {
        while(string.IsNullOrWhiteSpace(line)) {
            line = inputReader.ReadLine();
            if(line == null) return null;
        }

        string word;
        line = line.Trim();
        var index = line.IndexOf(sep);
        if(index == -1) {
            word = line;
            line = null;
            return word;
        }

        word = line.Substring(0, index);
        line = line.Substring(index + 1);
        return word;
    }

    public void Reset() { line = null; InDefinition = false; nested = 0;}

    public static void CommentP(Word w, Translator tr) {
        string? word;
        do {
            word = tr.NextWord();
        } while(word != null && word != ")");
    }
    public static void CommentS(Word w, Translator tr) {
        tr.line = "";
    }

    public void Emit(string s) => output.AppendLine(s);

    private static void ExecuteDef(Word w, Translator tr) {
        w.def(w, tr);
    }
    public static void CompileOrEmit(Word w, Translator tr) {
        if (tr.InDefinition) Compile(w, tr); else ExecuteDef(w, tr);
    }
    public static void Compile(Word w, Translator tr) {
        if(w.immediate)
            ExecuteDef(w, tr);
        else if(tr.actions == null)
            throw new Exception($"Trying to compile {w} outside a definition");
        else
            tr.actions.Add(w);
    }
    public static void Perform(Word w, Translator tr) {
        if(tr.InDefinition) Compile(w, tr);
        else ExecuteDef(w, tr);
    }

    private static string NextWordNorm(Translator tr) {
        var s = tr.NextWord();
        if(s == null) throw new Exception($"End of input stream after {s}");
        s = s.ToLowerInvariant();
        s = ToCsharpId(s);
        return s;
    }

    public static void ColonDef(Word w, Translator tr) {
        var s = NextWordNorm(tr);

        tr.lastWord    = s;
        tr.defActions  = new();
        tr.doesActions = null;
        tr.actions     = tr.defActions;

        tr.Emit(ToCsharpInst("_labelHere", $"\"{s}\""));

        tr.InDefinition = true;
    }
    public static void CreateDef(Word w, Translator tr) {
        var s = NextWordNorm(tr);

        tr.lastCreated = s;
        tr.Emit(ToCsharpInst("_labelHere", $"\"{s}\""));

        // By default, calling a created word pushes the address on the stack.
        // This behavior can be overridden by does>, which is managed below.
        tr.words[s] = inline(ToCsharpInst("_pushLabel", $"\"{s}\""));
    }
    public static void DoesDef(Word w, Translator tr) {
        // Encountering does> finishes the definition of the word and starts the definition of does.
        tr.doesActions = new();
        tr.actions = tr.doesActions;
        tr.doesActions.Add(
                function((Word w, Translator tr1) => // tr1 is at time of does> execution.
                    tr1.Emit(ToCsharpInst("_pushLabel", $"\"{tr1.lastCreated}\"")), false));
    }
    public static void SemiColonDef(Word w, Translator tr) {
        if(tr.defActions == null) throw new Exception("Semicolon (;) seen in interpret mode");

         // Gnarly. At creation (i.e. 10 array ar) attach the words from does> part of : array to
         // the dictionary word for ar.
        if(tr.doesActions != null)
            tr.defActions.Add(function((Word w, Translator tr1) =>
                        tr1.words[tr1.lastCreated] = new Word { immediate = false,
                        export = false, def = ExecuteWords(tr.doesActions) }, false));

        var wordName = ToCsharpId(tr.lastWord);
        // Attach the definition actions to the defining word (: array)
        tr.words[wordName] = new Word { name = wordName, immediate = false,
            export = false, def = ExecuteWords(tr.defActions)};
        tr.InDefinition = false;
    }
    // Tried to use a c# constant instead of the dictionary for constants.
    // It didn't work because, to support Exit, a new definition is wrapped in a do {} while loop.
    // This makes the defined constant local to that scope, not visible outside it.
    // But it is faster, so making it a compile time flag.
    public static void ConstantDef(Word w, Translator tr) {
        var s = NextWordNorm(tr);

        tr.Emit($"readonly nint {s} = VmExt.pop(ref vm);");
        tr.words[s] = inline($"var a = {s};pusha;");
    }
    public static void ConstantDef1(Word w, Translator tr) {
        var s = NextWordNorm(tr);
        tr.Emit(ToCsharpInst("_labelHere", $"\"{s}\""));
        tr.Emit(ToCsharpInst("_comma"));
        var op1 = ToCsharpInst("_pushLabel",  $"\"{s}\"");
        var op2 = ToCsharpInst("_fetch");

        if(tr.words.TryGetValue(s, out var _)) throw new Exception($"Trying to reassign the constant {s}");
        tr.words[s] = inline($"{op1};{op2};");
    }
    // TODO; refactor away repetition in the next two functions.
    public static void TickDef(Word w, Translator tr) {
        var s = NextWordNorm(tr);
        var word = tr.words[s];

        if(!word.tickdefined) {
            tr.Emit($"void {s}() {{\n");
            ExecuteDef(word, tr);
            tr.Emit("\n}");

            tr.Emit($"vm.xts[vm.xtsp] = {s};vm.wordToXts[\"{s}\"] = vm.xtsp; VmExt.push(ref vm, vm.xtsp);vm.xtsp++;");
            word.tickdefined = true;
            tr.words[s] = word;
        } else {
            tr.Emit($"VmExt.push(ref vm, vm.wordToXts[\"{s}\"]);");
        }
    }
    public static void TickDefIm(Word w, Translator tr) {
        var s = NextWordNorm(tr);
        var word = tr.words[s];

        if(!word.tickdefined) {
            tr.Emit($"void {s}() {{\n");
            ExecuteDef(word, tr);
            tr.Emit("\n}");

            tr.Emit($"vm.xts[vm.xtsp] = {s};vm.wordToXts[\"{s}\"] = vm.xtsp; VmExt.push(ref vm, vm.xtsp);vm.xtsp++;");
            word.tickdefined = true;
            tr.words[s] = word;
        } 
        Compile(verbatim($"VmExt.push(ref vm, vm.wordToXts[\"{s}\"]);"), tr);
    }
    public static void immediateDef(Word w, Translator tr) {
        var word = tr.words[tr.lastWord];
        word.immediate = true;
        tr.words[tr.lastWord] = word;
    }
    public static void dotString(Word w, Translator tr) {
        var s = tr.NextWord('"');
        if(s == null) throw new Exception("End of input stream after .\"");

        CompileOrEmit(function((Word w, Translator tr1) => tr1.Emit($"vm.output.WriteLine(\"{s}\");"), false), tr);
    }
    public static void abort(Word w, Translator tr) {
        var s = tr.NextWord('"');
        if(s == null) throw new Exception("End of input stream after abort\"");

        void f(Word w, Translator tr1) {
            tr1.Emit($"if(VmExt.pop(ref vm) != 0) throw new Exception(\"{s}\");");
        }
        CompileOrEmit(function(f, false), tr);
    }
    public static void charIm(Word w, Translator tr) {
        var s = tr.NextWord();
        if(s == null) throw new Exception("End of input stream after [char]");
        CompileOrEmit(function((Word w, Translator tr1) => tr1.Emit($"VmExt.push(ref vm, {(int)s[0]});"), false), tr);
    }
    public static void charN(Word w, Translator tr) {
        var s = tr.NextWord();
        if(s == null) throw new Exception("End of input stream after char");
        tr.Emit($"VmExt.push(ref vm, {(int)s[0]});");
    }
    public static void DoDef(Word w, Translator tr) {

        tr.nested++;

        var i = $"___i{tr.nested}";
        var s = $"___s{tr.nested}";
        var e = $"___e{tr.nested}";
        tr.Emit($@"var {s} = VmExt.pop(ref vm);
var {e} = VmExt.pop(ref vm);
for(var {i} = {s};{i} < {e}; {i}++) {{
");
    }
    public static void LoopPlusDef(Word w, Translator tr) {
        var i = $"___i{tr.nested}";
        tr.Emit($"{{ var a = VmExt.pop(ref vm); {i} += a;}};}}");
    }

    public static void IDef(Word w, Translator tr) {
        
        var i = $"___i{tr.nested}";
        tr.Emit($"VmExt.push(ref vm, {i});");
    }
    public static void JDef(Word w, Translator tr) {
        
        var i = $"___i{tr.nested - 1}";
        tr.Emit($"VmExt.push(ref vm, {i});");
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

    public static string ToCsharpInst(string inst, string arg) {
        if(!IsIdentifier(inst)) throw new Exception($"{inst} not an identifier");
        return $"VmExt.{inst}(ref vm, {arg});";
    }
    public static string ToInstStream(string words) => String.Join(";\n", words.Split(';').Select(ToCsharpInst));

    public static string ToCsharpId(string forthId) {
        StringBuilder sb = new();
        foreach(var c in forthId)
            if(sym.TryGetValue(c, out var v)) sb.Append($"_{v}");
            else sb.Append(c);
        return sb.ToString();
    }

    public static Definition ExecuteWords(IEnumerable<Word> words) =>
        (Word w, Translator tr) => {
            // Ugly way to support return. Kind of simulate a subroutine call in a peephole optimization.
            // TODO: test it is optimized away as it should.
            if(!tr.InDefinition) tr.Emit("do {\n");

            foreach(var word in words) ExecuteDef(word, tr);

            if(!tr.InDefinition) tr.Emit("} while(false);\n");
        };

    public static Word inline(string instructions) => new Word {
        immediate = false, export = false, def = (word, tr) => {
            var fullInst = $"{{\n{ToInstStream(instructions)}\n}}";
            tr.Emit(fullInst);
        }
    };
    public static Word verbatim(string text) => new Word {
        immediate = false, export = false, def = (word, tr) => {
            tr.Emit(text);
        }
    };
    public static Word intrinsic(string name) => new Word {
        immediate = false, export = true, def = (word, tr) => {
            var csharp = ToCsharpInst(name);
            tr.Emit(csharp);
        }
    };
    public static Word function(Definition f, bool immediate) => new Word {
        immediate = immediate, export = true, def = f };

    public static void PushNumber(string n, Translator tr) {

// Having to support the bonkers base feature in Forth slows things down as every push needs to be
// base-converted. You can disable base aware input by defining NOBASE.
#if NOBASE
        var s = $"VmExt.push(ref vm, {n});";
#else
        var s = $"VmExt.pushs(ref vm, \"{n}\");";
#endif
        tr.Emit(s);
    }
    public static void CompileNumber(string word, Translator tr) {
        if(tr.actions == null) throw new Exception($"Compiling {word}, found null actions property");

        tr.actions.Add(new Word { immediate = false, export = false, def = (Word w, Translator tr) =>
                PushNumber(word, tr) });
    }
    public static void PerformNumber(string aNumber, Translator tr) {
        if(tr.InDefinition) CompileNumber(aNumber, tr);
        else PushNumber(aNumber, tr);
    }
    // At compile time we don't know the base and the size of the cell (nint) for the Forth VM.
    // We try them all and get a runtime exception if we guess wrong.
    private static bool IsANumberInAnyBase(string? s) {
        foreach(var b in new int[] { 2, 8, 10, 16 }) { // Screw you, base 9.
            try { var _ = (nint)Convert.ToSByte(s, b); return true;} catch(Exception) { }
            try { var _ = (nint)Convert.ToInt16(s, b); return true;} catch(Exception) { }
            try { var _ = (nint)Convert.ToInt32(s, b); return true;} catch(Exception) { }
            try { var _ = (nint)Convert.ToInt64(s, b); return true;} catch(Exception) { }
        }
        return false;
    }
    public static void TranslateWord(string word, Translator tr) {
        word = word.ToLowerInvariant();
        if(tr.words.TryGetValue(word, out var v)) { // Keep symbols (i.e, +, -).
            Perform(v, tr);
        } else if(tr.words.TryGetValue(ToCsharpId(word), out var vc)) { // Transform symbols to C#.
            Perform(vc, tr);
        } else if(IsANumberInAnyBase(word)) {
            PerformNumber(word, tr);
        } else {
            throw new Exception($"Word '{word}' not in the dictionary.");
        }
    }
    public static void InsertDo(string word, Translator tr) {
        tr.Emit(ToCsharpInst("_do", $"\"{word}\""));
    }

    public static void Translate(Translator tr) {
        while(true) {
            var word = tr.NextWord();
            if(word == null) break;

            TranslateWord(word, tr);
        }
    }
    public static string TranslateString(string forthCode) {
        var outp = new StringBuilder();
        var tr = new Translator(new StringReader(forthCode), outp);
        Translate(tr);
        return tr.output.ToString();
    }

    public static string ToCSharp(string funcName, string outp) =>
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
    static public void {funcName} (ref Vm vm) {{
        {outp}
    }}
}}
";
    public static Word binary(string op) => inline($"popa;popb;var c = b {op} a;pushc;");
    public static Word intbinary(string op) => inline($"popa;popb;var c = (int)b {op} (int)a;pushc;");
    public static Word unary(string op)  => inline($"popa;var c = {op}(a);pushc;");
    public static Word unaryp(string op)  => inline($"popa;var c = (a){op};pushc;");
    public static Word math2(string op)  => inline($"popa;popb;var c = Math.{op}(b, a);pushc;");
    public static Word comp(string op) => inline($"popa;popb;var f = b {op} a;var c = f ? -1 : 0;pushc;");
    public static Word compu(string op) => inline($"popa;var f = a {op};var c = f ? -1 : 0;pushc;");
    public static Word pusha(string op) => inline($"var a = {op};pusha;");

    // The Forth dictionary.
    public Dictionary<string , Word> words = new() {
            {"+"             , binary("+") }         ,
            {"-"             , binary("-") }         ,
            {"*"             , binary("*") }         ,
            {"/"             , binary("/") }         ,
            {"mod"           , binary("%") }         ,
            {"and"           , binary("&") }         ,
            {"or"            , binary("|") }         ,
            {"xor"           , binary("^") }         ,
            {"lshift"        , intbinary("<<") }        ,
            {"rshift"        , intbinary(">>") }        ,
            {"negate"        , unary("-") }          ,
            {"1+"            , unary("++") }         ,
            {"1-"            , unary("--") }         ,
            {"2*"            , unary("2 *") }         ,
            {"2/"            , unary("2 /") }         ,
            {"abs"           , unary("Math.Abs") }   ,
            {"min"           , math2("Min") }        ,
            {"max"           , math2("Max") }        ,
            {"="           , comp("==") }        ,
            {"<>"           , comp("!=") }        ,
            {"<"           , comp("<") }        ,
            {"<="           , comp("<=") }        ,
            {">"           , comp(">") }        ,
            {">="           , comp(">=") }        ,
            {"0="           , compu("== 0") }        ,
            {"0<>"           , compu("!= 0") }        ,
            {"0<"           , compu("< 0") }        ,
            {"0<="           , compu("<= 0") }        ,
            {"0>"           , compu("> 0") }        ,
            {"0>="           , compu(">= 0") }        ,
            {"true"           , pusha("-1") }        ,
            {"false"           , pusha("0") }        ,

            {"dup"     ,  intrinsic("dup")},
            {"dup2"    ,  intrinsic("dup2")},
            {"drop"    ,  intrinsic("drop")},
            {"drop2"   ,  intrinsic("drop2")},
            {"cells"   ,  intrinsic("cells")},
            {"cell+"   ,  intrinsic("cellp")},
            {"chars"   ,  intrinsic("chars")},
            {"char+"   ,  intrinsic("charp")},
            {"unused"    ,  intrinsic("unused")},
            {"here"    ,  intrinsic("here")},
            {"over"    ,  intrinsic("over")},
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
            {"_do"     ,  intrinsic("_do")},
            {".s"   ,  intrinsic("_dots")},
            {"dump"    ,  intrinsic("dump")},
            {"?dup"    ,  intrinsic("_qdup")},
            {"depth"    ,  intrinsic("depth")},
            {">r"    ,  intrinsic("toR")},
            {"r>"    ,  intrinsic("fromR")},
            {"r@"    ,  intrinsic("fetchR")},
            {"+!"    ,  intrinsic("_fetchP")},
            {"move"    ,  intrinsic("move")},
            {"cmove"    ,  intrinsic("cmove")},
            {"cmove>"    ,  intrinsic("cmove")},
            {"fill"    ,  intrinsic("fill")},
            {"blank"    ,  intrinsic("blank")},
            {"erase"    ,  intrinsic("erase")},
            {"u.r"    ,  intrinsic("urdot")},

            {"_labelHere"      ,  intrinsic("_labelHere")},

            {"create" , function(CreateDef    , false)} ,
            {"variable" , function(CreateDef    , false)} ,
#if FASTCONSTANT
            {"constant" , function(ConstantDef    , false)} ,
#else
            {"constant" , function(ConstantDef1    , false)} ,
#endif
            {":"      , function(ColonDef     , false)} ,
            {";"      , function(SemiColonDef , true)}  ,
            {"does>"  , function(DoesDef      , true)}  ,
            {"("  , function(CommentP      , true)}  ,
            {"\\"  , function(CommentS      , true)}  ,
            {"do"  , function(DoDef      , false)}  ,
            {"+loop"  , function(LoopPlusDef      , false)}  ,
            {"loop"  , verbatim("}")}  ,
            {"i"  , function(IDef      , false)}  ,
            {"j"  , function(JDef      , false)}  ,
            {".\""  , function(dotString      , true)}  ,
            {"[char]"  , function(charIm      , true)}  ,
            {"char"  , function(charN      , false)}  ,
            {"abort\""  , function(abort      , true)}  ,
            {"'"  , function(TickDef      , false)}  ,
            {"[']"  , function(TickDefIm      , true)}  ,
            {"exit"  , verbatim("break;\n")}  ,
            {"immediate"  , function(immediateDef, false)},
            {"["  , function((Word w, Translator tr) => tr.InDefinition = false, true)},
            {"]"  , function((Word w, Translator tr) => tr.InDefinition = true, true)},

            {"."       ,   inline("_dot;")},
            {"cr"      ,   inline("vm.output.WriteLine();")},
            {"swap"      ,   inline("popa;popb;pusha;pushb;")},
            {"rot"      ,   inline("popa;popb;popc;pushb;pusha;pushc;")},
            {"base"      ,   inline("basepu;")},
            {"decimal"      ,   inline("var a = 10;pusha;basepu;_store;")},
            {"hex"      ,   inline("var a = 16;pusha;basepu;_store;")},
            {"?"      ,   inline("_fetch;_dot;")},
            {"pad"      ,   inline("var a = vm.pad;pusha;")},

            {"if"      ,   verbatim("if(VmExt.pop(ref vm) != 0) {")},
            {"else"      ,   verbatim("} else {")},
            {"then"      ,   verbatim("}")},
            {"endif"      ,   verbatim("}")},

            {"begin"      ,   verbatim("while(true) {")},
            {"repeat"      ,   verbatim("}")},
            {"again"      ,   verbatim("}")},
            {"while"      ,   verbatim("if(VmExt.pop(ref vm) == 0) break;")},
            {"until"      ,   verbatim("if(VmExt.pop(ref vm) != 0) break; }")},
            {"leave"      ,   verbatim("break;")},
            {"page"      ,   verbatim("vm.output.Clear();")},
            {"spaces"      ,   inline("popa;for(var i = 0; i < a; i++) vm.output.Write(' ');")},
            {"space"      ,   inline("vm.output.Write(' ');")},
            {"emit"      ,   inline("popa;vm.output.Write((char)a);")},
            {"execute"      ,   inline("popa;vm.xts[a]();")},
        };

    // Maps symbols to words
    public static Dictionary<char, string> sym = new() {
        {'+', "plus"},
        {'-', "minus"},
        {'>', "more"},
        {'<', "less"},
        {'=', "equal"},
        {'!', "store"},
        {'@', "fetch"},
        {'"', "apostr"},
        {'%', "percent"},
        {'$', "dollar"},
        {'*', "mult"},
        {'(', "oparens"},
        {')', "cparens"},
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
        {"sstring", "var s = VmExt.dotNetString(ref vm)"},
    };
}
