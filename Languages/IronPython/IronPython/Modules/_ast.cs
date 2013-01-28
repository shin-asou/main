/* ****************************************************************************
 *
 * Copyright (c) Jeff Hardy 2010. 
 * Copyright (c) Dan Eloff 2008-2009. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironpy@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IronPython.Compiler;
using IronPython.Compiler.Ast;
using IronPython.Runtime;
using IronPython.Runtime.Operations;
using IronPython.Runtime.Types;
using IronPython.Runtime.Exceptions;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

using PyOperator = IronPython.Compiler.PythonOperator;
using PythonList = IronPython.Runtime.List;
using System.Runtime.InteropServices;

#if FEATURE_NUMERICS
using System.Numerics;
#else
using Microsoft.Scripting.Math;
using Complex = Microsoft.Scripting.Math.Complex64;
#endif

[assembly: PythonModule("_ast", typeof(IronPython.Modules._ast))]
namespace IronPython.Modules
{
    public static class _ast
    {
        public const string __version__ = "62047";
        public const int PyCF_ONLY_AST = 0x400;

        private class ThrowingErrorSink : ErrorSink
        {
            public static new readonly ThrowingErrorSink/*!*/ Default = new ThrowingErrorSink();

            private ThrowingErrorSink() {
            }

            public override void Add(SourceUnit sourceUnit, string message, SourceSpan span, int errorCode, Severity severity) {
                if (severity == Severity.Warning) {
                    PythonOps.SyntaxWarning(message, sourceUnit, span, errorCode);
                } else {
                    throw PythonOps.SyntaxError(message, sourceUnit, span, errorCode);
                }
            }
        }

        internal static AST BuildAst(CodeContext context, SourceUnit sourceUnit, PythonCompilerOptions opts, string mode) {
            Parser parser = Parser.CreateParser(
                new CompilerContext(sourceUnit, opts, ThrowingErrorSink.Default),
                (PythonOptions)context.LanguageContext.Options);

            PythonAst ast = parser.ParseFile(true);
            return ConvertToAST(ast, mode);
        }

        private static mod ConvertToAST(PythonAst pythonAst, string kind) {
            ContractUtils.RequiresNotNull(pythonAst, "pythonAst");
            ContractUtils.RequiresNotNull(kind, "kind");
            return ConvertToAST((SuiteStatement)pythonAst.Body, kind);
        }

        private static mod ConvertToAST(SuiteStatement suite, string kind) {
            ContractUtils.RequiresNotNull(suite, "suite");
            ContractUtils.RequiresNotNull(kind, "kind");
            switch (kind) {
                case "exec":
                    return new Module(suite);
                case "eval":
                    return new Expression(suite);
                case "single":
                    return new Interactive(suite);
                default:
                    throw new ArgumentException("kind must be 'exec' or 'eval' or 'single'");
            }
        }

        private static stmt ConvertToAST(Statement stmt) {
            ContractUtils.RequiresNotNull(stmt, "stmt");
            return AST.Convert(stmt);
        }

        private static expr ConvertToAST(Compiler.Ast.Expression expr) {
            ContractUtils.RequiresNotNull(expr, "expr");
            return AST.Convert(expr);
        }

        [PythonType]
        public abstract class AST
        {
            private PythonTuple __fields = new PythonTuple();   // Genshi assumes _fields in not None
            private PythonTuple __attributes = new PythonTuple();   // Genshi assumes _fields in not None
            protected int? _lineno; // both lineno and col_offset are expected to be int, in cpython anything is accepted
            protected int? _col_offset;

            public PythonTuple _fields {
                get { return __fields; }
                protected set { __fields = value; }
            }

            public PythonTuple _attributes {
                get { return __attributes; }
                protected set { __attributes = value; }
            }

            public int lineno {
                get { 
                    if (_lineno != null) return (int)_lineno;
                    throw PythonOps.AttributeErrorForMissingAttribute(PythonTypeOps.GetName(this), "lineno");
                }
                set { _lineno = value; }
            }

            public int col_offset {
                get { 
                    if (_col_offset != null) return (int)_col_offset;
                    throw PythonOps.AttributeErrorForMissingAttribute(PythonTypeOps.GetName(this), "col_offset");
                }
                set { _col_offset = value; }
            }

            public void __setstate__(PythonDictionary state) {
                restoreProperties(__attributes, state);
                restoreProperties(__fields, state);
            }

            internal void restoreProperties(IEnumerable<object> names, IDictionary source) {
                foreach (object name in names) {
                    if (name is string) {
                        try {
                            string key = (string)name;
                            this.GetType().GetProperty(key).SetValue(this, source[key], null);
                        } catch (System.Collections.Generic.KeyNotFoundException) {
                            // ignore missing
                        }
                    }
                }
            }

            internal void storeProperties(IEnumerable<object> names, IDictionary target) {
                foreach (object name in names) {
                    if (name is string) {
                        string key = (string)name;
                        object val;
                        try {
                            val = this.GetType().GetProperty(key).GetValue(this, null);
                            target.Add(key, val);
                        } catch (System.Reflection.TargetInvocationException) {
                            // field not set
                        }
                    }
                }
            }

            internal PythonDictionary getstate() {
                PythonDictionary d = new PythonDictionary(10);
                storeProperties(__fields, d);
                storeProperties(__attributes, d);
                return d;
            }

            public virtual object/*!*/ __reduce__() {
                return PythonTuple.MakeTuple(DynamicHelpers.GetPythonType(this), new PythonTuple(), getstate());
            }

            public virtual object/*!*/ __reduce_ex__(int protocol) {
                return __reduce__();
            }

            protected void GetSourceLocation(Node node) {
                _lineno = node.Start.Line;

                // IronPython counts from 1; CPython counts from 0
                _col_offset = node.Start.Column - 1;
            }

            internal static PythonList ConvertStatements(Statement stmt) {
                return ConvertStatements(stmt, false);
            }

            internal static PythonList ConvertStatements(Statement stmt, bool allowNull) {
                if (stmt == null)
                    if (allowNull)
                        return PythonOps.MakeEmptyList(0);
                    else
                        throw new ArgumentNullException("stmt");

                if (stmt is SuiteStatement) {
                    SuiteStatement suite = (SuiteStatement)stmt;
                    PythonList l = PythonOps.MakeEmptyList(suite.Statements.Count);
                    foreach (Statement s in suite.Statements)
                        l.Add(Convert(s));

                    return l;
                }

                return PythonOps.MakeListNoCopy(Convert(stmt));
            }

            internal static stmt Convert(Statement stmt) {
                stmt ast;

                if (stmt is FunctionDefinition)
                    ast = new FunctionDef((FunctionDefinition)stmt);
                else if (stmt is ReturnStatement)
                    ast = new Return((ReturnStatement)stmt);
                else if (stmt is AssignmentStatement)
                    ast = new Assign((AssignmentStatement)stmt);
                else if (stmt is AugmentedAssignStatement)
                    ast = new AugAssign((AugmentedAssignStatement)stmt);
                else if (stmt is DelStatement)
                    ast = new Delete((DelStatement)stmt);
                else if (stmt is PrintStatement)
                    ast = new Print((PrintStatement)stmt);
                else if (stmt is ExpressionStatement)
                    ast = new Expr((ExpressionStatement)stmt);
                else if (stmt is ForStatement)
                    ast = new For((ForStatement)stmt);
                else if (stmt is WhileStatement)
                    ast = new While((WhileStatement)stmt);
                else if (stmt is IfStatement)
                    ast = new If((IfStatement)stmt);
                else if (stmt is WithStatement)
                    ast = new With((WithStatement)stmt);
                else if (stmt is RaiseStatement)
                    ast = new Raise((RaiseStatement)stmt);
                else if (stmt is TryStatement)
                    ast = Convert((TryStatement)stmt);
                else if (stmt is AssertStatement)
                    ast = new Assert((AssertStatement)stmt);
                else if (stmt is ImportStatement)
                    ast = new Import((ImportStatement)stmt);
                else if (stmt is FromImportStatement)
                    ast = new ImportFrom((FromImportStatement)stmt);
                else if (stmt is ExecStatement)
                    ast = new Exec((ExecStatement)stmt);
                else if (stmt is GlobalStatement)
                    ast = new Global((GlobalStatement)stmt);
                else if (stmt is ClassDefinition)
                    ast = new ClassDef((ClassDefinition)stmt);
                else if (stmt is BreakStatement)
                    ast = new Break();
                else if (stmt is ContinueStatement)
                    ast = new Continue();
                else if (stmt is EmptyStatement)
                    ast = new Pass();
                else
                    throw new ArgumentTypeException("Unexpected statement type: " + stmt.GetType());

                ast.GetSourceLocation(stmt);
                return ast;
            }

            internal static stmt Convert(TryStatement stmt) {
                if (stmt.Finally != null) {
                    PythonList body;
                    if (stmt.Handlers != null && stmt.Handlers.Count != 0)
                        body = PythonOps.MakeListNoCopy(new TryExcept(stmt));
                    else
                        body = ConvertStatements(stmt.Body);

                    return new TryFinally(body, ConvertStatements(stmt.Finally));
                }

                return new TryExcept(stmt);
            }

            internal static PythonList ConvertAliases(IList<DottedName> names, IList<string> asnames) {
                PythonList l = PythonOps.MakeEmptyList(names.Count);

                if (names == FromImportStatement.Star)
                    l.Add(new alias("*", null));
                else
                    for (int i = 0; i < names.Count; i++)
                        l.Add(new alias(names[i].MakeString(), asnames[i]));

                return l;
            }

            internal static PythonList ConvertAliases(IList<string> names, IList<string> asnames) {
                PythonList l = PythonOps.MakeEmptyList(names.Count);

                if (names == FromImportStatement.Star)
                    l.Add(new alias("*", null));
                else
                    for (int i = 0; i < names.Count; i++)
                        l.Add(new alias(names[i], asnames[i]));

                return l;
            }

            internal static slice TrySliceConvert(Compiler.Ast.Expression expr) {
                if (expr is SliceExpression)
                    return new Slice((SliceExpression)expr);
                if (expr is ConstantExpression && ((ConstantExpression)expr).Value == PythonOps.Ellipsis)
                    return Ellipsis.Instance;
                if (expr is TupleExpression && ((TupleExpression)expr).IsExpandable)
                    return new ExtSlice(((Tuple)Convert(expr)).elts);
                return null;
            }

            internal static expr Convert(Compiler.Ast.Expression expr) {
                return Convert(expr, Load.Instance);
            }

            internal static expr Convert(Compiler.Ast.Expression expr, expr_context ctx) {
                expr ast;

                if (expr is ConstantExpression)
                    ast = Convert((ConstantExpression)expr);
                else if (expr is NameExpression)
                    ast = new Name((NameExpression)expr, ctx);
                else if (expr is UnaryExpression)
                    ast = new UnaryOp((UnaryExpression)expr);
                else if (expr is BinaryExpression)
                    ast = Convert((BinaryExpression)expr);
                else if (expr is AndExpression)
                    ast = new BoolOp((AndExpression)expr);
                else if (expr is OrExpression)
                    ast = new BoolOp((OrExpression)expr);
                else if (expr is CallExpression)
                    ast = new Call((CallExpression)expr);
                else if (expr is ParenthesisExpression)
                    return Convert(((ParenthesisExpression)expr).Expression);
                else if (expr is LambdaExpression)
                    ast = new Lambda((LambdaExpression)expr);
                else if (expr is ListExpression)
                    ast = new List((ListExpression)expr, ctx);
                else if (expr is TupleExpression)
                    ast = new Tuple((TupleExpression)expr, ctx);
                else if (expr is DictionaryExpression)
                    ast = new Dict((DictionaryExpression)expr);
                else if (expr is ListComprehension)
                    ast = new ListComp((ListComprehension)expr);
                else if (expr is GeneratorExpression)
                    ast = new GeneratorExp((GeneratorExpression)expr);
                else if (expr is MemberExpression)
                    ast = new Attribute((MemberExpression)expr, ctx);
                else if (expr is YieldExpression)
                    ast = new Yield((YieldExpression)expr);
                else if (expr is ConditionalExpression)
                    ast = new IfExp((ConditionalExpression)expr);
                else if (expr is IndexExpression)
                    ast = new Subscript((IndexExpression)expr, ctx);
                else if (expr is BackQuoteExpression)
                    ast = new Repr((BackQuoteExpression)expr);
                else if (expr is SetExpression)
                    ast = new Set((SetExpression)expr);
                else if (expr is DictionaryComprehension)
                    ast = new DictComp((DictionaryComprehension)expr);
                else if (expr is SetComprehension)
                    ast = new SetComp((SetComprehension)expr);
                else
                    throw new ArgumentTypeException("Unexpected expression type: " + expr.GetType());

                ast.GetSourceLocation(expr);
                return ast;
            }

            internal static expr Convert(ConstantExpression expr) {
                expr ast;

                if (expr.Value == null)
                    return new Name("None", Load.Instance);

                if (expr.Value is int || expr.Value is double || expr.Value is Int64 || expr.Value is BigInteger || expr.Value is Complex)
                    ast = new Num(expr.Value);
                else if (expr.Value is string)
                    ast = new Str((string)expr.Value);
                else if (expr.Value is IronPython.Runtime.Bytes)
                    ast = new Str(Converter.ConvertToString(expr.Value));

                else
                    throw new ArgumentTypeException("Unexpected constant type: " + expr.Value.GetType());

                return ast;
            }

            internal static expr Convert(BinaryExpression expr) {
                AST op = Convert(expr.Operator);
                if (BinaryExpression.IsComparison(expr)) {
                    return new Compare(expr);
                } else {
                    if (op is @operator) {
                        return new BinOp(expr, (@operator)op);
                    }
                }

                throw new ArgumentTypeException("Unexpected operator type: " + op.GetType());
            }

            internal static AST Convert(Node node) {
                AST ast;

                if (node is TryStatementHandler)
                    ast = new ExceptHandler((TryStatementHandler)node);
                else
                    throw new ArgumentTypeException("Unexpected node type: " + node.GetType());

                ast.GetSourceLocation(node);
                return ast;
            }

            internal static PythonList Convert(IList<ComprehensionIterator> iters) {
                ComprehensionIterator[] iters2 = new ComprehensionIterator[iters.Count];
                iters.CopyTo(iters2, 0);
                return Convert(iters2);
            }

            internal static PythonList Convert(ComprehensionIterator[] iters) {
                PythonList comps = new PythonList();
                int start = 1;
                for (int i = 0; i < iters.Length; i++) {
                    if (i == 0 || iters[i] is ComprehensionIf)
                        if (i == iters.Length - 1)
                            i++;
                        else
                            continue;

                    ComprehensionIf[] ifs = new ComprehensionIf[i - start];
                    Array.Copy(iters, start, ifs, 0, ifs.Length);
                    comps.Add(new comprehension((ComprehensionFor)iters[start - 1], ifs));
                    start = i + 1;
                }
                return comps;
            }

            internal static AST Convert(PyOperator op) {
                // We treat operator classes as singletons here to keep overhead down
                // But we cannot fully make them singletons if we wish to keep compatibility wity CPython
                switch (op) {
                    case PyOperator.Add:
                        return Add.Instance;
                    case PyOperator.BitwiseAnd:
                        return BitAnd.Instance;
                    case PyOperator.BitwiseOr:
                        return BitOr.Instance;
                    case PyOperator.Divide:
                        return Div.Instance;
                    case PyOperator.Equal:
                        return Eq.Instance;
                    case PyOperator.FloorDivide:
                        return FloorDiv.Instance;
                    case PyOperator.GreaterThan:
                        return Gt.Instance;
                    case PyOperator.GreaterThanOrEqual:
                        return GtE.Instance;
                    case PyOperator.In:
                        return In.Instance;
                    case PyOperator.Invert:
                        return Invert.Instance;
                    case PyOperator.Is:
                        return Is.Instance;
                    case PyOperator.IsNot:
                        return IsNot.Instance;
                    case PyOperator.LeftShift:
                        return LShift.Instance;
                    case PyOperator.LessThan:
                        return Lt.Instance;
                    case PyOperator.LessThanOrEqual:
                        return LtE.Instance;
                    case PyOperator.Mod:
                        return Mod.Instance;
                    case PyOperator.Multiply:
                        return Mult.Instance;
                    case PyOperator.Negate:
                        return USub.Instance;
                    case PyOperator.Not:
                        return Not.Instance;
                    case PyOperator.NotEqual:
                        return NotEq.Instance;
                    case PyOperator.NotIn:
                        return NotIn.Instance;
                    case PyOperator.Pos:
                        return UAdd.Instance;
                    case PyOperator.Power:
                        return Pow.Instance;
                    case PyOperator.RightShift:
                        return RShift.Instance;
                    case PyOperator.Subtract:
                        return Sub.Instance;
                    case PyOperator.Xor:
                        return BitXor.Instance;
                    default:
                        throw new ArgumentException("Unexpected PyOperator: " + op, "op");
                }
            }
        }

        [PythonType]
        public class alias : AST
        {
            private string _name;
            private string _asname;  // Optional

            public alias() {
                _fields = new PythonTuple(new[] { "name", "asname" });
            }

            internal alias(string name, [Optional]string asname)
                : this() {
                _name = name;
                _asname = asname;
            }

            public string name {
                get { return _name; }
                set { _name = value; }
            }

            public string asname {
                get { return _asname; }
                set { _asname = value; }
            }
        }

        [PythonType]
        public class arguments : AST
        {
            private PythonList _args;
            private string _vararg; // Optional
            private string _kwarg; // Optional
            private PythonList _defaults;

            public arguments() {
                _fields = new PythonTuple(new[] { "args", "vararg", "kwarg", "defaults" });
            }

            public arguments(PythonList args, [Optional]string vararg, [Optional]string kwarg, PythonList defaults)
                :this() {
                _args = args;
                _vararg = vararg;
                _kwarg = kwarg;
                _kwarg = kwarg;
                _defaults = defaults;
            }

            internal arguments(IList<Parameter> parameters)
                : this() {
                _args = PythonOps.MakeEmptyList(parameters.Count);
                _defaults = PythonOps.MakeEmptyList(parameters.Count);
                foreach (Parameter param in parameters) {
                    if (param.IsList)
                        _vararg = param.Name;
                    else if (param.IsDictionary)
                        _kwarg = param.Name;
                    else {
                        args.Add(new Name(param.Name, Param.Instance));
                        if (param.DefaultValue != null)
                            defaults.Add(Convert(param.DefaultValue));
                    }
                }
            }


            internal arguments(Parameter[] parameters)
                : this(parameters as IList<Parameter>) {
            }

            public PythonList args {
                get { return _args; }
                set { _args = value; }
            }

            public string vararg {
                get { return _vararg; }
                set { _vararg = value; }
            }

            public string kwarg {
                get { return _kwarg; }
                set { _kwarg = value; }
            }

            public PythonList defaults {
                get { return _defaults; }
                set { _defaults = value; }
            }
        }

        [PythonType]
        public abstract class boolop : AST
        {
        }

        [PythonType]
        public abstract class cmpop : AST
        {
        }

        [PythonType]
        public class comprehension : AST
        {
            private expr _target;
            private expr _iter;
            private PythonList _ifs;

            public comprehension() {
                _fields = new PythonTuple(new[] { "target", "iter", "ifs" });
            }

            public comprehension(expr target, expr iter, PythonList ifs)
                : this() {
                _target = target;
                _iter = iter;
                _ifs = ifs;
            }

            internal comprehension(ComprehensionFor listFor, ComprehensionIf[] listIfs)
                : this() {
                _target = Convert(listFor.Left, Store.Instance);
                _iter = Convert(listFor.List);
                _ifs = PythonOps.MakeEmptyList(listIfs.Length);
                foreach (ComprehensionIf listIf in listIfs)
                    _ifs.Add(Convert(listIf.Test));
            }

            public expr target {
                get { return _target; }
                set { _target = value; }
            }

            public expr iter {
                get { return _iter; }
                set { _iter = value; }
            }

            public PythonList ifs {
                get { return _ifs; }
                set { _ifs = value; }
            }
        }

        [PythonType]
        public class excepthandler : AST
        {
            public excepthandler() {
                _attributes = new PythonTuple(new[] { "lineno", "col_offset" });
            }
        }

        [PythonType]
        public abstract class expr : AST
        {
            protected expr() {
                _attributes = new PythonTuple(new[] { "lineno", "col_offset" });
            }
        }

        [PythonType]
        public abstract class expr_context : AST
        {
        }

        [PythonType]
        public class keyword : AST
        {
            private string _arg;
            private expr _value;

            public keyword() {
                _fields = new PythonTuple(new[] { "arg", "value" });
            }

            public keyword(string arg, expr value)
                : this() {
                _arg = arg;
                _value = value;
            }

            internal keyword(IronPython.Compiler.Ast.Arg arg)
                : this() {
                _arg = arg.Name;
                _value = Convert(arg.Expression);
            }

            public string arg {
                get { return _arg; }
                set { _arg = value; }
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }

        [PythonType]
        public abstract class mod : AST
        {
            internal abstract PythonList GetStatements();
        }

        [PythonType]
        public abstract class @operator : AST
        {
        }

        [PythonType]
        public abstract class slice : AST
        {
        }

        [PythonType]
        public abstract class stmt : AST
        {
            protected stmt() {
                _attributes = new PythonTuple(new[] { "lineno", "col_offset" });
            }
        }

        [PythonType]
        public abstract class unaryop : AST
        {
        }

        [PythonType]
        public class Add : @operator
        {
            internal static Add Instance = new Add();
        }

        [PythonType]
        public class And : boolop
        {
            internal static And Instance = new And();
        }

        [PythonType]
        public class Assert : stmt
        {
            private expr _test;
            private expr _msg; // Optional

            public Assert() {
                _fields = new PythonTuple(new[] { "test", "msg" });
            }

            public Assert(expr test, expr msg, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _test = test;
                _msg = msg;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Assert(AssertStatement stmt)
                : this() {
                _test = Convert(stmt.Test);
                if (stmt.Message != null)
                    _msg = Convert(stmt.Message);
            }

            public expr test {
                get { return _test; }
                set { _test = value; }
            }

            public expr msg {
                get { return _msg; }
                set { _msg = value; }
            }
        }

        [PythonType]
        public class Assign : stmt
        {
            private PythonList _targets;
            private expr _value;

            public Assign() {
                _fields = new PythonTuple(new[] { "targets", "value" });
            }

            public Assign(PythonList targets, expr value, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _targets = targets;
                _value = value;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Assign(AssignmentStatement stmt)
                : this() {
                _targets = PythonOps.MakeEmptyList(stmt.Left.Count);
                foreach (Compiler.Ast.Expression expr in stmt.Left)
                    _targets.Add(Convert(expr, Store.Instance));

                _value = Convert(stmt.Right);
            }

            public PythonList targets {
                get { return _targets; }
                set { _targets = value; }
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }

        [PythonType]
        public class Attribute : expr
        {
            private expr _value;
            private string _attr;
            private expr_context _ctx;

            public Attribute() {
                _fields = new PythonTuple(new[] { "value", "attr", "ctx" });
            }

            public Attribute(expr value, string attr, expr_context ctx,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _value = value;
                _attr = attr;
                _ctx = ctx;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Attribute(MemberExpression attr, expr_context ctx)
                : this() {
                _value = Convert(attr.Target);
                _attr = attr.Name;
                _ctx = ctx;
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }

            public string attr {
                get { return _attr; }
                set { _attr = value; }
            }

            public expr_context ctx {
                get { return _ctx; }
                set { _ctx = value; }
            }
        }

        [PythonType]
        public class AugAssign : stmt
        {
            private expr _target;
            private @operator _op;
            private expr _value;

            public AugAssign() {
                _fields = new PythonTuple(new[] { "target", "op", "value" });
            }

            public AugAssign(expr target, @operator op, expr value,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _target = target;
                _op = op;
                _value = value;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal AugAssign(AugmentedAssignStatement stmt)
                : this() {
                _target = Convert(stmt.Left, Store.Instance);
                _value = Convert(stmt.Right);
                _op = (@operator)Convert(stmt.Operator);
            }

            public expr target {
                get { return _target; }
                set { _target = value; }
            }

            public @operator op {
                get { return _op; }
                set { _op = value; }
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }

        /// <summary>
        /// Not used.
        /// </summary>
        [PythonType]
        public class AugLoad : expr_context
        {
        }

        /// <summary>
        /// Not used.
        /// </summary>
        [PythonType]
        public class AugStore : expr_context
        {
        }

        [PythonType]
        public class BinOp : expr
        {
            private expr _left;
            private expr _right;
            private @operator _op;

            public BinOp() {
                _fields = new PythonTuple(new[] { "left", "op", "right" });
            }

            public BinOp(expr left, @operator op, expr right, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _left = left;
                _op = op;
                _right = right;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal BinOp(BinaryExpression expr, @operator op)
                : this() {
                _left = Convert(expr.Left);
                _right = Convert(expr.Right);
                _op = op;
            }

            public expr left {
                get { return _left; }
                set { _left = value; }
            }

            public expr right {
                get { return _right; }
                set { _right = value; }
            }

            public @operator op {
                get { return _op; }
                set { _op = value; }
            }
        }

        [PythonType]
        public class BitAnd : @operator
        {
            internal static BitAnd Instance = new BitAnd();
        }

        [PythonType]
        public class BitOr : @operator
        {
            internal static BitOr Instance = new BitOr();
        }

        [PythonType]
        public class BitXor : @operator
        {
            internal static BitXor Instance = new BitXor();
        }

        [PythonType]
        public class BoolOp : expr
        {
            private boolop _op;
            private PythonList _values;

            public BoolOp() {
                _fields = new PythonTuple(new[] { "op", "values" });
            }

            public BoolOp(boolop op, PythonList values, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _op = op;
                _values = values;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal BoolOp(AndExpression and)
                : this() {
                _values = PythonOps.MakeListNoCopy(Convert(and.Left), Convert(and.Right));
                _op = And.Instance;
            }

            internal BoolOp(OrExpression or)
                : this() {
                _values = PythonOps.MakeListNoCopy(Convert(or.Left), Convert(or.Right));
                _op = Or.Instance;
            }

            public boolop op {
                get { return _op; }
                set { _op = value; }
            }

            public PythonList values {
                get { return _values; }
                set { _values = value; }
            }
        }

        [PythonType]
        public class Break : stmt
        {
            internal static Break Instance = new Break();

            internal Break()
                : this(null, null) { }

            public Break([Optional]int? lineno, [Optional]int? col_offset) {
                _lineno = lineno;
                _col_offset = col_offset;
            }
        }

        [PythonType]
        public class Call : expr
        {
            private expr _func;
            private PythonList _args;
            private PythonList _keywords;
            private expr _starargs; // Optional
            private expr _kwargs; // Optional

            public Call() {
                _fields = new PythonTuple(new[] { "func", "args", "keywords", "starargs", "kwargs" });
            }

            public Call( expr func, PythonList args, PythonList keywords, 
                [Optional]expr starargs, [Optional]expr kwargs,
                [Optional]int? lineno, [Optional]int? col_offset) 
                :this() {
                _func = func;
                _args = args;
                _keywords = keywords;
                _starargs = starargs;
                _kwargs = kwargs;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Call(CallExpression call)
                : this() {
                _args = PythonOps.MakeEmptyList(call.Args.Count);
                _keywords = new PythonList();
                _func = Convert(call.Target);
                foreach (IronPython.Compiler.Ast.Arg arg in call.Args) {

                    if (arg.Name == null)
                        _args.Add(Convert(arg.Expression));
                    else if (arg.Name == "*")
                        _starargs = Convert(arg.Expression);
                    else if (arg.Name == "**")
                        _kwargs = Convert(arg.Expression);
                    else
                        _keywords.Add(new keyword(arg));
                }
            }

            public expr func {
                get { return _func; }
                set { _func = value; }
            }

            public PythonList args {
                get { return _args; }
                set { _args = value; }
            }

            public PythonList keywords {
                get { return _keywords; }
                set { _keywords = value; }
            }

            public expr starargs {
                get { return _starargs; }
                set { _starargs = value; }
            }

            public expr kwargs {
                get { return _kwargs; }
                set { _kwargs = value; }
            }
        }

        [PythonType]
        public class ClassDef : stmt
        {
            private string _name;
            private PythonList _bases;
            private PythonList _body;
            private PythonList _decorator_list;

            public ClassDef() {
                _fields = new PythonTuple(new[] { "name", "bases", "body", "decorator_list" });
            }

            public ClassDef(string name, PythonList bases, PythonList body, PythonList decorator_list,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _name = name;
                _bases = bases;
                _body = body;
                _decorator_list = decorator_list;
                _lineno = lineno;
                _col_offset = col_offset;
            }


            internal ClassDef(ClassDefinition def)
                : this() {
                _name = def.Name;
                _bases = PythonOps.MakeEmptyList(def.Bases.Count);
                foreach (Compiler.Ast.Expression expr in def.Bases)
                    _bases.Add(Convert(expr));
                _body = ConvertStatements(def.Body);
                _decorator_list = new PythonList(); // TODO Actually fill in the decorators here
            }

            public string name {
                get { return _name; }
                set { _name = value; }
            }

            public PythonList bases {
                get { return _bases; }
                set { _bases = value; }
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            public PythonList decorator_list {
                get { return _decorator_list; }
                set { _decorator_list = value; }
            }
        }

        [PythonType]
        public class Compare : expr
        {
            private expr _left;
            private PythonList _ops;
            private PythonList _comparators;

            public Compare() {
                _fields = new PythonTuple(new[] { "left", "ops", "comparators" });
            }

            public Compare(expr left, PythonList ops, PythonList comparators, 
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _left = left;
                _ops = ops;
                _comparators = comparators;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Compare(BinaryExpression expr)
                : this() {
                _left = Convert(expr.Left);
                _ops = PythonOps.MakeList();
                _comparators = PythonOps.MakeList();
                while (BinaryExpression.IsComparison(expr.Right)) {
                    BinaryExpression right = (BinaryExpression)expr.Right;
                    // start accumulating ops and comparators
                    _ops.Add(Convert(expr.Operator));
                    _comparators.Add(Convert(right.Left));
                    expr = right;
                }
                _ops.Add(Convert(expr.Operator));
                _comparators.Add(Convert(expr.Right));
            }

            public expr left {
                get { return _left; }
                set { _left = value; }
            }

            public PythonList ops {
                get { return _ops; }
                set { _ops = value; }
            }

            public PythonList comparators {
                get { return _comparators; }
                set { _comparators = value; }
            }
        }

        [PythonType]
        public class Continue : stmt
        {
            internal static Continue Instance = new Continue();

            internal Continue()
                : this(null, null) { }

            public Continue([Optional]int? lineno, [Optional]int? col_offset) {
                _lineno = lineno;
                _col_offset = col_offset;
            }
        }

        [PythonType]
        public class Del : expr_context
        {
            internal static Del Instance = new Del();
        }

        [PythonType]
        public class Delete : stmt
        {
            private PythonList _targets;

            public Delete() {
                _fields = new PythonTuple(new[] { "targets", });
            }

            public Delete(PythonList targets, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _targets = targets;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Delete(DelStatement stmt)
                : this() {
                _targets = PythonOps.MakeEmptyList(stmt.Expressions.Count);
                foreach (Compiler.Ast.Expression expr in stmt.Expressions)
                    _targets.Add(Convert(expr, Del.Instance));
            }

            public PythonList targets {
                get { return _targets; }
                set { _targets = value; }
            }
        }

        [PythonType]
        public class Dict : expr
        {
            private PythonList _keys;
            private PythonList _values;

            public Dict() {
                _fields = new PythonTuple(new[] { "keys", "values" });
            }

            public Dict(PythonList keys, PythonList values, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _keys = keys;
                _values = values;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Dict(DictionaryExpression expr)
                : this() {
                _keys = PythonOps.MakeEmptyList(expr.Items.Count);
                _values = PythonOps.MakeEmptyList(expr.Items.Count);
                foreach (SliceExpression item in expr.Items) {
                    _keys.Add(Convert(item.SliceStart));
                    _values.Add(Convert(item.SliceStop));
                }
            }

            public PythonList keys {
                get { return _keys; }
                set { _keys = value; }
            }

            public PythonList values {
                get { return _values; }
                set { _values = value; }
            }
        }

        [PythonType]
        public class DictComp : expr {
            private expr _key;
            private expr _value;
            private PythonList _generators;

            public DictComp() {
                _fields = new PythonTuple(new[] { "key", "value", "generators" });
            }

            public DictComp(expr key, expr value, PythonList generators, 
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _key = key;
                _value = value;
                _generators = generators;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal DictComp(DictionaryComprehension comp)
                : this() {
                _key = Convert(comp.Key);
                _value = Convert(comp.Value);
                _generators = Convert(comp.Iterators);
            }

            public expr key {
                get { return _key; }
                set { _key = value; }
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }

            public PythonList generators {
                get { return _generators; }
                set { _generators = value; }
            }
        }


        [PythonType]
        public class Div : @operator
        {
            internal static Div Instance = new Div();
        }

        [PythonType]
        public class Ellipsis : slice
        {
            internal static Ellipsis Instance = new Ellipsis();
        }

        [PythonType]
        public class Eq : cmpop
        {
            internal static Eq Instance = new Eq();
        }

        [PythonType]
        public class ExceptHandler : excepthandler
        {
            private expr _type;
            private expr _name;
            private PythonList _body;

            public ExceptHandler() {
                _fields = new PythonTuple(new[] { "type", "name", "body" });
            }

            public ExceptHandler([Optional]expr type, [Optional]expr name, PythonList body,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _type = type;
                _name = name;
                _body = body;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal ExceptHandler(TryStatementHandler stmt)
                : this() {
                if (stmt.Test != null)
                    _type = Convert(stmt.Test);
                if (stmt.Target != null)
                    _name = Convert(stmt.Target, Store.Instance);

                _body = ConvertStatements(stmt.Body);
            }

            public expr type {
                get { return _type; }
                set { _type = value; }
            }

            public expr name {
                get { return _name; }
                set { _name = value; }
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }
        }

        [PythonType]
        public class Exec : stmt
        {
            private expr _body;
            private expr _globals; // Optional
            private expr _locals; // Optional

            public Exec() {
                _fields = new PythonTuple(new[] { "body", "globals", "locals" });
            }

            public Exec(expr body, [Optional]expr globals, [Optional]expr locals,
               [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _body = body;
                _globals = globals;
                _locals = locals;
                _lineno = lineno;
                _col_offset = col_offset;
            }


            public Exec(ExecStatement stmt)
                : this() {
                _body = Convert(stmt.Code);
                if (stmt.Globals != null)
                    _globals = Convert(stmt.Globals);
                if (stmt.Locals != null)
                    _locals = Convert(stmt.Locals);
            }

            public expr body {
                get { return _body; }
                set { _body = value; }
            }

            public expr globals {
                get { return _globals; }
                set { _globals = value; }
            }

            public expr locals {
                get { return _locals; }
                set { _locals = value; }
            }
        }

        [PythonType]
        public class Expr : stmt
        {
            private expr _value;

            public Expr() {
                _fields = new PythonTuple(new[] { "value", });
            }

            public Expr(expr value,  [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _value = value;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Expr(ExpressionStatement stmt)
                : this() {
                _value = Convert(stmt.Expression);
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }

        [PythonType]
        public class Expression : mod
        {
            private expr _body;

            public Expression() {
                _fields = new PythonTuple(new[] { "body", });
            }

            public Expression(expr body)
                : this() {
                _body = body;
            }

            internal Expression(SuiteStatement suite)
                : this() {
                _body = Convert(((ExpressionStatement)suite.Statements[0]).Expression);
            }

            public expr body {
                get { return _body; }
                set { _body = value; }
            }

            internal override PythonList GetStatements() {
                return PythonOps.MakeListNoCopy(_body);
            }
        }

        [PythonType]
        public class ExtSlice : slice
        {
            private PythonList _dims;

            public ExtSlice() {
                _fields = new PythonTuple(new[] { "dims", });
            }

            public ExtSlice(PythonList dims)
                : this() {
                _dims = dims;
            }

            public PythonList dims {
                get { return _dims; }
                set { _dims = value; }
            }
        }

        [PythonType]
        public class FloorDiv : @operator
        {
            internal static FloorDiv Instance = new FloorDiv();
        }

        [PythonType]
        public class For : stmt
        {
            private expr _target;
            private expr _iter;
            private PythonList _body;
            private PythonList _orelse; // Optional, default []

            public For() {
                _fields = new PythonTuple(new[] { "target", "iter", "body", "orelse" });
            }

            public For(expr target, expr iter, PythonList body, [Optional]PythonList orelse,
               [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _target = target;
                _iter = iter;
                _body = body;
                if (null == orelse)
                    _orelse = new PythonList();
                else
                    _orelse = orelse;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal For(ForStatement stmt)
                : this() {
                _target = Convert(stmt.Left, Store.Instance);
                _iter = Convert(stmt.List);
                _body = ConvertStatements(stmt.Body);
                _orelse = ConvertStatements(stmt.Else, true);
            }

            public expr target {
                get { return _target; }
                set { _target = value; }
            }

            public expr iter {
                get { return _iter; }
                set { _iter = value; }
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            public PythonList orelse {
                get { return _orelse; }
                set { _orelse = value; }
            }
        }

        [PythonType]
        public class FunctionDef : stmt
        {
            private string _name;
            private arguments _args;
            private PythonList _body;
            private PythonList _decorators;

            public FunctionDef() {
                _fields = new PythonTuple(new[] { "name", "args", "body", "decorators" });
            }

            public FunctionDef(string name, arguments args, PythonList body, PythonList decorators,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _name = name;
                _args = args;
                _body = body;
                _decorators = decorators;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal FunctionDef(FunctionDefinition def)
                : this() {
                _name = def.Name;
                _args = new arguments(def.Parameters);
                _body = ConvertStatements(def.Body);

                if (def.Decorators != null) {
                    _decorators = PythonOps.MakeEmptyList(def.Decorators.Count);
                    foreach (Compiler.Ast.Expression expr in def.Decorators)
                        _decorators.Add(Convert(expr));
                } else
                    _decorators = PythonOps.MakeEmptyList(0);
            }

            public string name {
                get { return _name; }
                set { _name = value; }
            }

            public arguments args {
                get { return _args; }
                set { _args = value; }
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            public PythonList decorators {
                get { return _decorators; }
                set { _decorators = value; }
            }
        }

        [PythonType]
        public class GeneratorExp : expr
        {
            private expr _elt;
            private PythonList _generators;

            public GeneratorExp() {
                _fields = new PythonTuple(new[] { "elt", "generators" });
            }

            public GeneratorExp(expr elt, PythonList generators, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _elt = elt;
                _generators = generators;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal GeneratorExp(GeneratorExpression expr)
                : this() {
                ExtractListComprehensionIterators walker = new ExtractListComprehensionIterators();
                expr.Function.Body.Walk(walker);
                ComprehensionIterator[] iters = walker.Iterators;
                Debug.Assert(iters.Length != 0, "A generator expression cannot have zero iterators.");
                iters[0] = new ComprehensionFor(((ComprehensionFor)iters[0]).Left, expr.Iterable);
                _elt = Convert(walker.Yield.Expression);
                _generators = Convert(iters);
            }

            public expr elt {
                get { return _elt; }
                set { _elt = value; }
            }

            public PythonList generators {
                get { return _generators; }
                set { _generators = value; }
            }


            internal class ExtractListComprehensionIterators : PythonWalker
            {
                private readonly List<ComprehensionIterator> _iterators = new List<ComprehensionIterator>();
                public YieldExpression Yield;

                public ComprehensionIterator[] Iterators {
                    get { return _iterators.ToArray(); }
                }

                public override bool Walk(ForStatement node) {
                    _iterators.Add(new ComprehensionFor(node.Left, node.List));
                    node.Body.Walk(this);
                    return false;
                }

                public override bool Walk(IfStatement node) {
                    _iterators.Add(new ComprehensionIf(node.Tests[0].Test));
                    node.Tests[0].Body.Walk(this);
                    return false;
                }

                public override bool Walk(YieldExpression node) {
                    Yield = node;
                    return false;
                }
            }
        }

        [PythonType]
        public class Global : stmt
        {
            private PythonList _names;

            public Global() {
                _fields = new PythonTuple(new[] { "names", });
            }

            public Global(PythonList names, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _names = names;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Global(GlobalStatement stmt)
                : this() {
                _names = new PythonList(stmt.Names);
            }

            public PythonList names {
                get { return _names; }
                set { _names = value; }
            }
        }

        [PythonType]
        public class Gt : cmpop
        {
            internal static Gt Instance = new Gt();
        }

        [PythonType]
        public class GtE : cmpop
        {
            internal static GtE Instance = new GtE();
        }

        [PythonType]
        public class If : stmt
        {
            private expr _test;
            private PythonList _body;
            private PythonList _orelse; // Optional, default []

            public If() {
                _fields = new PythonTuple(new[] { "test", "body", "orelse" });
            }

            public If(expr test, PythonList body, [Optional]PythonList orelse, 
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _test = test;
                _body = body;
                if (null == orelse)
                    _orelse = new PythonList();
                else
                    _orelse = orelse;
            }

            internal If(IfStatement stmt)
                : this() {
                If current = this;
                If parent = null;
                foreach (IfStatementTest ifTest in stmt.Tests) {
                    if (parent != null) {
                        current = new If();
                        parent._orelse = PythonOps.MakeListNoCopy(current);
                    }

                    current.Initialize(ifTest);
                    parent = current;
                }

                current._orelse = ConvertStatements(stmt.ElseStatement, true);
            }

            internal void Initialize(IfStatementTest ifTest) {
                _test = Convert(ifTest.Test);
                _body = ConvertStatements(ifTest.Body);
            }

            public expr test {
                get { return _test; }
                set { _test = value; }
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            public PythonList orelse {
                get { return _orelse; }
                set { _orelse = value; }
            }
        }

        [PythonType]
        public class IfExp : expr
        {
            private expr _test;
            private expr _body;
            private expr _orelse;

            public IfExp() {
                _fields = new PythonTuple(new[] { "test", "body", "orelse" });
            }

            public IfExp(expr test, expr body, expr orelse, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _test = test;
                _body = body;
                _orelse = orelse;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal IfExp(ConditionalExpression cond)
                : this() {
                _test = Convert(cond.Test);
                _body = Convert(cond.TrueExpression);
                _orelse = Convert(cond.FalseExpression);
            }

            public expr test {
                get { return _test; }
                set { _test = value; }
            }

            public expr body {
                get { return _body; }
                set { _body = value; }
            }

            public expr orelse {
                get { return _orelse; }
                set { _orelse = value; }
            }
        }

        [PythonType]
        public class Import : stmt
        {
            private PythonList _names;

            public Import() {
                _fields = new PythonTuple(new[] { "names", });
            }

            public Import(PythonList names, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _names = names;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Import(ImportStatement stmt)
                : this() {
                _names = ConvertAliases(stmt.Names, stmt.AsNames);
            }

            public PythonList names {
                get { return _names; }
                set { _names = value; }
            }
        }

        [PythonType]
        public class ImportFrom : stmt
        {
            private string _module; // Optional
            private PythonList _names;
            private int _level; // Optional, default 0

            public ImportFrom() {
                _fields = new PythonTuple(new[] { "module", "names", "level" });
            }

            public ImportFrom([Optional]string module, PythonList names, [Optional]int level,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _module = module;
                _names = names;
                _level = level;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            public ImportFrom(FromImportStatement stmt)
                : this() {
                _module = stmt.Root.MakeString();
                _module = string.IsNullOrEmpty(_module) ? null : _module;
                _names = ConvertAliases(stmt.Names, stmt.AsNames);
                if (stmt.Root is RelativeModuleName)
                    _level = ((RelativeModuleName)stmt.Root).DotCount;
            }

            public string module {
                get { return _module; }
                set { _module = value; }
            }

            public PythonList names {
                get { return _names; }
                set { _names = value; }
            }

            public int level {
                get { return _level; }
                set { _level = value; }
            }
        }

        [PythonType]
        public class In : cmpop
        {
            internal static In Instance = new In();
        }

        [PythonType]
        public class Index : slice
        {
            private expr _value;

            public Index() {
                _fields = new PythonTuple(new[] { "value", });
            }

            public Index(expr value)
                : this() {
                _value = value;
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }

        [PythonType]
        public class Interactive : mod
        {
            private PythonList _body;

            public Interactive() {
                _fields = new PythonTuple(new[] { "body", });
            }

            public Interactive(PythonList body)
                : this() {
                _body = body;
            }

            internal Interactive(SuiteStatement suite)
                : this() {
                _body = ConvertStatements(suite);
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            internal override PythonList GetStatements() {
                return _body;
            }
        }

        [PythonType]
        public class Invert : unaryop
        {
            internal static Invert Instance = new Invert();
        }

        [PythonType]
        public class Is : cmpop
        {
            internal static Is Instance = new Is();
        }

        [PythonType]
        public class IsNot : cmpop
        {
            internal static IsNot Instance = new IsNot();
        }

        [PythonType]
        public class Lambda : expr
        {
            private arguments _args;
            private expr _body;

            public Lambda() {
                _fields = new PythonTuple(new[] { "args", "body" });
            }

            public Lambda(arguments args, expr body, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _args = args;
                _body = body;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Lambda(LambdaExpression lambda)
                : this() {
                FunctionDef def = (FunctionDef)Convert(lambda.Function);
                _args = def.args;
                Debug.Assert(def.body.Count == 1, "LambdaExpression body should be one Return statement.");
                _body = ((Return)def.body[0]).value;
            }

            public arguments args {
                get { return _args; }
                set { _args = value; }
            }

            public expr body {
                get { return _body; }
                set { _body = value; }
            }
        }

        [PythonType]
        public class List : expr
        {
            private PythonList _elts;
            private expr_context _ctx;

            public List() {
                _fields = new PythonTuple(new[] { "elts", "ctx" });
            }

            public List(PythonList elts, expr_context ctx, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _elts = elts;
                _ctx = ctx;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal List(ListExpression list, expr_context ctx)
                : this() {
                _elts = PythonOps.MakeEmptyList(list.Items.Count);
                foreach (Compiler.Ast.Expression expr in list.Items)
                    _elts.Add(Convert(expr, ctx));

                _ctx = ctx;
            }

            public PythonList elts {
                get { return _elts; }
                set { _elts = value; }
            }

            public expr_context ctx {
                get { return _ctx; }
                set { _ctx = value; }
            }
        }

        [PythonType]
        public class ListComp : expr
        {
            private expr _elt;
            private PythonList _generators;

            public ListComp() {
                _fields = new PythonTuple(new[] { "elt", "generators" });
            }

            public ListComp(expr elt, PythonList generators, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _elt = elt;
                _generators = generators;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal ListComp(ListComprehension comp)
                : this() {
                _elt = Convert(comp.Item);
                _generators = Convert(comp.Iterators);
            }

            public expr elt {
                get { return _elt; }
                set { _elt = value; }
            }

            public PythonList generators {
                get { return _generators; }
                set { _generators = value; }
            }
        }

        [PythonType]
        public class Load : expr_context
        {
            internal static Load Instance = new Load();
        }

        [PythonType]
        public class Lt : cmpop
        {
            internal static Lt Instance = new Lt();
        }

        [PythonType]
        public class LtE : cmpop
        {
            internal static LtE Instance = new LtE();
        }

        [PythonType]
        public class LShift : @operator
        {
            internal static LShift Instance = new LShift();
        }

        [PythonType]
        public class Mod : @operator
        {
            internal static Mod Instance = new Mod();
        }

        [PythonType]
        public class Module : mod
        {
            private PythonList _body;

            public Module() {
                _fields = new PythonTuple(new[] { "body", });
            }

            public Module(PythonList body)
                : this() {
                _body = body;
            }

            internal Module(SuiteStatement suite)
                : this() {
                _body = ConvertStatements(suite);
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            internal override PythonList GetStatements() {
                return _body;
            }
        }

        [PythonType]
        public class Mult : @operator
        {
            internal static Mult Instance = new Mult();
        }

        [PythonType]
        public class Name : expr
        {
            private string _id;
            private expr_context _ctx;

            public Name() {
                _fields = new PythonTuple(new[] { "id", "ctx" });
            }

            public Name(string id, expr_context ctx, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _id = id;
                _ctx = ctx;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            public Name(String id, expr_context ctx)
                : this(id, ctx, null, null) { }

            internal Name(NameExpression expr, expr_context ctx)
                : this(expr.Name, ctx) {
            }

            public expr_context ctx {
                get { return _ctx; }
                set { _ctx = value; }
            }

            public string id {
                get { return _id; }
                set { _id = value; }
            }
        }

        [PythonType]
        public class Not : unaryop
        {
            internal static Not Instance = new Not();
        }

        [PythonType]
        public class NotEq : cmpop
        {
            internal static NotEq Instance = new NotEq();
        }

        [PythonType]
        public class NotIn : cmpop
        {
            internal static NotIn Instance = new NotIn();
        }

        [PythonType]
        public class Num : expr
        {
            private object _n;

            public Num() {
                _fields = new PythonTuple(new[] { "n", });
            }

            internal Num(object n)
                : this(n, null, null) { }

            public Num(object n, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _n = n;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            public object n {
                get { return _n; }
                set { _n = value; }
            }
        }

        [PythonType]
        public class Or : boolop
        {
            internal static Or Instance = new Or();
        }

        [PythonType]
        public class Param : expr_context
        {
            internal static Param Instance = new Param();
        }

        [PythonType]
        public class Pass : stmt
        {
            internal static Pass Instance = new Pass();

            internal Pass()
                : this(null, null) { }

            public Pass([Optional]int? lineno, [Optional]int? col_offset) {
                _lineno = lineno;
                _col_offset = col_offset;
            }
        }

        [PythonType]
        public class Pow : @operator
        {
            internal static Pow Instance = new Pow();
        }

        [PythonType]
        public class Print : stmt
        {
            private expr _dest; // optional
            private PythonList _values;
            private bool _nl;

            public Print() {
                _fields = new PythonTuple(new[] { "dest", "values", "nl" });
            }

            public Print([Optional]expr dest, PythonList values, bool nl,
               [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _dest = dest;
                _values = values;
                _nl = nl;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Print(PrintStatement stmt)
                : this() {
                if (stmt.Destination != null)
                    _dest = Convert(stmt.Destination);

                _values = PythonOps.MakeEmptyList(stmt.Expressions.Count);
                foreach (Compiler.Ast.Expression expr in stmt.Expressions)
                    _values.Add(Convert(expr));

                _nl = !stmt.TrailingComma;
            }

            public expr dest {
                get { return _dest; }
                set { _dest = value; }
            }

            public PythonList values {
                get { return _values; }
                set { _values = value; }
            }

            public bool nl {
                get { return _nl; }
                set { _nl = value; }
            }
        }

        [PythonType]
        public class Raise : stmt
        {
            private expr _type; // Optional
            private expr _inst; // Optional
            private expr _tback; // Optional

            public Raise() {
                _fields = new PythonTuple(new[] { "type", "inst", "tback" });
            }

            public Raise([Optional]expr type, [Optional]expr inst, [Optional]expr tback,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _type = type;
                _inst = inst;
                _tback = tback;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Raise(RaiseStatement stmt)
                : this() {
                if (stmt.ExceptType != null)
                    _type = Convert(stmt.ExceptType);
                if (stmt.Value != null)
                    _inst = Convert(stmt.Value);
                if (stmt.Traceback != null)
                    _tback = Convert(stmt.Traceback);
            }

            public expr type {
                get { return _type; }
                set { _type = value; }
            }

            public expr inst {
                get { return _inst; }
                set { _inst = value; }
            }

            public expr tback {
                get { return _tback; }
                set { _tback = value; }
            }
        }

        [PythonType]
        public class Repr : expr
        {
            private expr _value;

            public Repr() {
                _fields = new PythonTuple(new[] { "value", });
            }

            public Repr(expr value, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _value = value;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Repr(BackQuoteExpression expr)
                : this() {
                _value = Convert(expr.Expression);
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }

        [PythonType]
        public class Return : stmt
        {
            private expr _value; // Optional

            public Return() {
                _fields = new PythonTuple(new[] { "value", });
            }

            public Return([Optional]expr value, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _value = value;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            public Return(ReturnStatement statement)
                : this() {
                // statement.Expression is never null
                //or is it?
                if (statement.Expression == null)
                    _value = null;
                else
                    _value = Convert(statement.Expression);
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }

        [PythonType]
        public class RShift : @operator
        {
            internal static RShift Instance = new RShift();
        }

        [PythonType]
        public class Set : expr {
            private PythonList _elts;

            public Set() {
                _fields = new PythonTuple(new[] { "elts" });
            }

            public Set(PythonList elts, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _elts = elts;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Set(SetExpression setExpression)
                : this() {
                _elts = new PythonList(setExpression.Items.Count);
                foreach (Compiler.Ast.Expression item in setExpression.Items) {
                    _elts.Add(Convert(item));
                }
            }

            public PythonList elts {
                get { return _elts; }
                set { _elts = value; }
            }
        }

        [PythonType]
        public class SetComp : expr {
            private expr _elt;
            private PythonList _generators;

            public SetComp() {
                _fields = new PythonTuple(new[] { "elt", "generators" });
            }

            public SetComp(expr elt, PythonList generators, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _elt = elt;
                _generators = generators;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal SetComp(SetComprehension comp)
                : this() {  
                _elt = Convert(comp.Item);
                _generators = Convert(comp.Iterators);
            }

            public expr elt {
                get { return _elt; }
                set { _elt = value; }
            }

            public PythonList generators {
                get { return _generators; }
                set { _generators = value; }
            }
        }


        [PythonType]
        public class Slice : slice
        {
            private expr _lower; // Optional
            private expr _upper; // Optional
            private expr _step; // Optional

            public Slice() {
                _fields = new PythonTuple(new[] { "lower", "upper", "step" });
            }


            public Slice([Optional]expr lower, [Optional]expr upper, [Optional]expr step)
                // default interpretation of missing step is [:]
                // in order to get [::], please provide explicit Name('None',Load.Instance)
                : this() {
                _lower = lower;
                _upper = upper;
                _step = step;
            }

            internal Slice(SliceExpression expr)
                : this() {
                if (expr.SliceStart != null)
                    _lower = Convert(expr.SliceStart);
                if (expr.SliceStop != null)
                    _upper = Convert(expr.SliceStop);
                if (expr.StepProvided)
                    if (expr.SliceStep != null)
                        _step = Convert(expr.SliceStep); // [x:y]
                    else
                        _step = new Name("None", Load.Instance); // [x:y:]
            }

            public expr lower {
                get { return _lower; }
                set { _lower = value; }
            }

            public expr upper {
                get { return _upper; }
                set { _upper = value; }
            }

            public expr step {
                get { return _step; }
                set { _step = value; }
            }
        }

        [PythonType]
        public class Store : expr_context
        {
            internal static Store Instance = new Store();
        }

        [PythonType]
        public class Str : expr
        {
            private string _s;

            public Str() {
                _fields = new PythonTuple(new[] { "s", });
            }

            internal Str(String s)
                : this(s, null, null) { }

            public Str(string s, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _s = s;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            public string s {
                get { return _s; }
                set { _s = value; }
            }
        }

        [PythonType]
        public class Sub : @operator
        {
            internal static Sub Instance = new Sub();
        }

        [PythonType]
        public class Subscript : expr
        {
            private expr _value;
            private slice _slice;
            private expr_context _ctx;

            public Subscript() {
                _fields = new PythonTuple(new[] { "value", "slice", "ctx" });
            }

            public Subscript( expr value, slice slice, expr_context ctx, 
                [Optional]int? lineno, [Optional]int? col_offset )
                : this() {
                _value = value;
                _slice = slice;
                _ctx = ctx;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Subscript(IndexExpression expr, expr_context ctx)
                : this() {
                _value = Convert(expr.Target);
                _ctx = ctx;
                _slice = TrySliceConvert(expr.Index);
                if (_slice == null)
                    _slice = new Index(Convert(expr.Index));
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }

            public slice slice {
                get { return _slice; }
                set { _slice = value; }
            }

            public expr_context ctx {
                get { return _ctx; }
                set { _ctx = value; }
            }
        }

        /// <summary>
        /// Not an actual node. We don't create this, but it's here for compatibility.
        /// </summary>
        [PythonType]
        public class Suite : mod
        {
            private PythonList _body;

            public Suite() {
                _fields = new PythonTuple(new[] { "body", });
            }

            public Suite(PythonList body)
                : this() {
                _body = body;
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            internal override PythonList GetStatements() {
                return _body;
            }
        }

        [PythonType]
        public class TryExcept : stmt
        {
            private PythonList _body;
            private PythonList _handlers;
            private PythonList _orelse; // Optional, default []

            public TryExcept() {
                _fields = new PythonTuple(new[] { "body", "handlers", "orelse" });
            }

            public TryExcept(PythonList body, PythonList handlers, [Optional]PythonList orelse,
                [Optional]int? lineno, [Optional]int? col_offset ) 
                : this() {
                _body = body;
                _handlers = handlers;
                if (null == orelse)
                    _orelse = new PythonList();
                else
                    _orelse = orelse;
                _lineno = lineno;
                _col_offset = col_offset;
            }


            internal TryExcept(TryStatement stmt)
                : this() {
                _body = ConvertStatements(stmt.Body);

                _handlers = PythonOps.MakeEmptyList(stmt.Handlers.Count);
                foreach (TryStatementHandler tryStmt in stmt.Handlers)
                    _handlers.Add(Convert(tryStmt));

                _orelse = ConvertStatements(stmt.Else, true);
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            public PythonList handlers {
                get { return _handlers; }
                set { _handlers = value; }
            }

            public PythonList orelse {
                get { return _orelse; }
                set { _orelse = value; }
            }
        }

        [PythonType]
        public class TryFinally : stmt
        {
            private PythonList _body;
            private PythonList _finalbody;

            public TryFinally() {
                _fields = new PythonTuple(new[] { "body", "finalbody" });
            }

            public TryFinally(PythonList body, PythonList finalBody, 
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _body = body;
                _finalbody = finalbody;
                _lineno = lineno;
                _col_offset = col_offset;
            }


            internal TryFinally(PythonList body, PythonList finalbody)
                : this() {
                _body = body;
                _finalbody = finalbody;
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            public PythonList finalbody {
                get { return _finalbody; }
                set { _finalbody = value; }
            }
        }

        [PythonType]
        public class Tuple : expr
        {
            private PythonList _elts;
            private expr_context _ctx;

            public Tuple() {
                _fields = new PythonTuple(new[] { "elts", "ctx" });
            }

            public Tuple(PythonList elts, expr_context ctx, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _elts = elts;
                _ctx = ctx;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Tuple(TupleExpression list, expr_context ctx)
                : this() {
                _elts = PythonOps.MakeEmptyList(list.Items.Count);
                foreach (Compiler.Ast.Expression expr in list.Items)
                    _elts.Add(Convert(expr, ctx));

                _ctx = ctx;
            }

            public PythonList elts {
                get { return _elts; }
                set { _elts = value; }
            }

            public expr_context ctx {
                get { return _ctx; }
                set { _ctx = value; }
            }
        }

        [PythonType]
        public class UnaryOp : expr
        {
            private unaryop _op;
            private expr _operand;

            public UnaryOp() {
                _fields = new PythonTuple(new[] { "op", "operand" });
            }

            internal UnaryOp(UnaryExpression expression)
                : this() {
                _op = (unaryop)Convert(expression.Op);
                _operand = Convert(expression.Expression);
            }

            public UnaryOp(unaryop op, expr operand, [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _op = op;
                _operand = operand;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            public unaryop op {
                get { return _op; }
                set { _op = value; }
            }

            public expr operand {
                get { return _operand; }
                set { _operand = value; }
            }
        }

        [PythonType]
        public class UAdd : unaryop
        {
            internal static UAdd Instance = new UAdd();
        }

        [PythonType]
        public class USub : unaryop
        {
            internal static USub Instance = new USub();
        }

        [PythonType]
        public class While : stmt
        {
            private expr _test;
            private PythonList _body;
            private PythonList _orelse; // Optional, default []

            public While() {
                _fields = new PythonTuple(new[] { "test", "body", "orelse" });
            }

            public While(expr test, PythonList body, [Optional]PythonList orelse,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _test = test;
                _body = body;
                if (null == orelse)
                    _orelse = new PythonList();
                else
                    _orelse = orelse;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal While(WhileStatement stmt)
                : this() {
                _test = Convert(stmt.Test);
                _body = ConvertStatements(stmt.Body);
                _orelse = ConvertStatements(stmt.ElseStatement, true);
            }

            public expr test {
                get { return _test; }
                set { _test = value; }
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }

            public PythonList orelse {
                get { return _orelse; }
                set { _orelse = value; }
            }
        }

        [PythonType]
        public class With : stmt
        {
            private expr _context_expr;
            private expr _optional_vars; // Optional
            private PythonList _body;

            public With() {
                _fields = new PythonTuple(new[] { "context_expr", "optional_vars", "body" });
            }

            public With(expr context_expr, [Optional]expr optional_vars, PythonList body,
                [Optional]int? lineno, [Optional]int? col_offset)
                : this() {
                _context_expr = context_expr;
                _optional_vars = optional_vars;
                _body = body;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal With(WithStatement with)
                : this() {
                _context_expr = Convert(with.ContextManager);
                if (with.Variable != null)
                    _optional_vars = Convert(with.Variable);

                _body = ConvertStatements(with.Body);
            }

            public expr context_expr {
                get { return _context_expr; }
                set { _context_expr = value; }
            }

            public expr optional_vars {
                get { return _optional_vars; }
                set { _optional_vars = value; }
            }

            public PythonList body {
                get { return _body; }
                set { _body = value; }
            }
        }

        [PythonType]
        public class Yield : expr
        {
            private expr _value; // Optional

            public Yield() {
                _fields = new PythonTuple(new[] { "value", });
            }

            public Yield([Optional]expr value, [Optional]int? lineno, [Optional]int? col_offset) 
                : this() {
                _value = value;
                _lineno = lineno;
                _col_offset = col_offset;
            }

            internal Yield(YieldExpression expr)
                : this() {
                // expr.Expression is never null
                _value = Convert(expr.Expression);
            }

            public expr value {
                get { return _value; }
                set { _value = value; }
            }
        }
    }
}
