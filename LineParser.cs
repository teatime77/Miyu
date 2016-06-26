﻿using System.Diagnostics;
using System.Collections.Generic;
using System;
using Windows.UI;
using System.IO;
using System.Net;
using System.Linq;
using System.Threading.Tasks;

/*--------------------------------------------------------------------------------
        1行の構文解析
--------------------------------------------------------------------------------*/

namespace MyEdit {
    partial class TParser {
        public const int TabSize = 4;
        public static TToken EOTToken = new TToken(ETokenType.White, EKind.EOT,"", 0, 0);
        public static TParser theParser;

        [ThreadStatic]
        public TClass LookaheadClass;

        public TProject PrjParser;
        public TToken[] TokenList;
        public int TokenPos;
        public TToken CurTkn;
        public TToken NextTkn;
        public bool Dirty;
        public bool Running;

        // キーワードの文字列の辞書
        public Dictionary<string, EKind> KeywordMap;

        // 2文字の記号の表
        public EKind[,] SymbolTable2 = new EKind[256, 256];

        // 1文字の記号の表
        public EKind[] SymbolTable1 = new EKind[256];

        public Dictionary<EKind, string> KindString = new Dictionary<EKind, string>();


        public TParser(TProject prj) {
            PrjParser = prj;

            // 字句解析の初期処理をします。
            InitializeLexicalAnalysis();
        }

        public string Tab(int nest) {
            return new string(' ', nest * TabSize);
        }

        public virtual void LPopt() {
        }

        public virtual void RPopt() {
        }

        public virtual void LCopt() {
        }

        public virtual void Colonopt() {
        }

        public TClass ReadEnumLine() {
            GetToken(EKind.enum_);
            TToken id = GetToken2(EKind.Identifier, EKind.ClassName);

            TClass cls = PrjParser.GetClassByName(id.TextTkn);
            cls.KindClass = EClass.Enum;

            LCopt();
            GetToken(EKind.EOT);

            return cls;
        }

        public TClass ReadClassLine() {
            GetToken2(EKind.class_, EKind.struct_);
            TToken id = GetToken2(EKind.Identifier, EKind.ClassName);

            TClass cls;

            if (CurTkn.Kind == EKind.LT) {
                // 総称型の場合

                List<TClass> param_classes = new List<TClass>();

                GetToken(EKind.LT);
                while (true) {
                    TToken param_name = GetToken(EKind.Identifier);

                    TClass param_class = new TClass(param_name.TextTkn);
                    param_class.GenericType = EGeneric.ArgumentClass;

                    param_classes.Add(param_class);

                    if (CurTkn.Kind != EKind.Comma) {

                        break;
                    }

                    GetToken(EKind.Comma);
                }
                GetToken(EKind.GT);

                TGenericClass parameterized_class = new TGenericClass(id.TextTkn, param_classes);

                parameterized_class.GenericType = EGeneric.ParameterizedClass;

                PrjParser.RegGenericClass(PrjParser.ParameterizedClassTable, parameterized_class);
                PrjParser.RegClass(PrjParser.ClassTable, parameterized_class);

                cls = parameterized_class;
            }
            else {

                cls = PrjParser.GetClassByName(id.TextTkn);
            }

            if (CurTkn.Kind == EKind.Colon) {

                GetToken(EKind.Colon);
                while (true) {

                    TToken super_class_name = GetToken2(EKind.Identifier, EKind.ClassName);

                    TClass super_class = PrjParser.GetClassByName(super_class_name.TextTkn);
                    cls.SuperClasses.Add(super_class);

                    if(CurTkn.Kind == EKind.Comma) {

                        GetToken(EKind.Comma);
                    }
                    else {

                        break;
                    }
                }
            }

            LCopt();
            GetToken(EKind.EOT);

            return cls;
        }

        public TField ReadFieldLine(TClass parent_class, bool is_static, TClass type_prepend) {
            TToken id = GetToken(EKind.Identifier);

            TClass tp;
            
            if(type_prepend != null) {

                tp = type_prepend;
            }
            else {

                GetToken(EKind.Colon);
                tp = ReadType(parent_class, false);
            }

            TTerm init = null;
            if (CurTkn.Kind == EKind.Assign) {

                GetToken(EKind.Assign);

                init = Expression();
            }

            LineEnd();

            return new TField(parent_class, is_static, id, tp, init);
        }

        public TField ReadEnumFieldLine(TClass parent_class) {
            TToken id = GetToken(EKind.Identifier);

            OptGetToken(EKind.Comma);
            GetToken(EKind.EOT);

            return new TField(parent_class, false, id, parent_class, null);
        }

        public TClass ReadType(TClass parent_class, bool new_class) {
            TToken id = GetToken2(EKind.Identifier, EKind.ClassName);
            TClass cls1 = PrjParser.GetParamClassByName(parent_class, id.TextTkn);

            List<TClass> param_classes = null;
            bool contains_argument_class = false;

            if (CurTkn.Kind == EKind.LT) {
                // 総称型の場合

                if (! (cls1 is TGenericClass)) {

                    throw new TParseException(cls1.ClassName + ":総称型以外に引数型があります。");
                }
                TGenericClass org_cla = cls1 as TGenericClass;

                param_classes = new List<TClass>();

                GetToken(EKind.LT);
                while (true) {
                    TClass param_class = ReadType(parent_class, false);

                    if (param_class.GenericType == EGeneric.ArgumentClass || param_class is TGenericClass && (parent_class as TGenericClass).ContainsArgumentClass) {

                        contains_argument_class = true;
                    }

                    param_classes.Add(param_class);

                    if(CurTkn.Kind != EKind.Comma) {

                        break;
                    }

                    GetToken(EKind.Comma);
                }
                GetToken(EKind.GT);

                if(org_cla.GenCla.Count != param_classes.Count) {

                    throw new TParseException("総称型の引数の数が一致しません。");
                }
            }

            int dim_cnt = 0;
            if (CurTkn.Kind == EKind.LB && ! new_class) {
                GetToken(EKind.LB);

                dim_cnt = 1;
                while (CurTkn.Kind == EKind.Comma) {
                    GetToken(EKind.Comma);
                    dim_cnt++;
                }

                GetToken(EKind.RB);
            }

            if (contains_argument_class) {
                // 引数にArgumentClassを含む場合

                TGenericClass tmp_class = new TGenericClass(cls1 as TGenericClass, param_classes);
                tmp_class.ContainsArgumentClass = true;
                tmp_class.DimCnt = dim_cnt;

                return tmp_class;
            }

            TClass cls2 = null;
            if (param_classes == null) {
                // 引数がない場合

                cls2 = cls1;
            }
            else {
                // 引数がある場合

                cls2 = GetSpecializedClass(cls1 as TGenericClass, param_classes);
            }

            if (dim_cnt == 0) {
                // 配列でない場合

                return cls2;
            }

            string class_text = cls2.GetClassText() + new string(',', dim_cnt - 1);

            TGenericClass reg_class;

            if (!PrjParser.ArrayClassTable.TryGetValue(class_text, out reg_class)) {

                reg_class = new TGenericClass(cls2, dim_cnt);
                reg_class.GenericType = EGeneric.SpecializedClass;

                //Debug.WriteLine("配列型 : {0}", reg_class.GetClassText(),"");
                PrjParser.ArrayClassTable.Add(class_text, reg_class);
            }

            return reg_class;
        }

        public TClass GetSpecializedClass(TGenericClass org_class, List<TClass> param_classes) {
            StringWriter sw = new StringWriter();
            sw.Write(org_class.ClassName);
            sw.Write("<");
            foreach(TClass c in param_classes) {
                if(c != param_classes[0]) {
                    sw.Write(",");
                }
                sw.Write("{0}", c.GetClassText());
            }
            sw.Write(">");

            string class_text = sw.ToString();

            TGenericClass reg_class;
            if (!PrjParser.SpecializedClassTable.TryGetValue(class_text, out reg_class)) {

                reg_class = new TGenericClass(org_class, param_classes);
                reg_class.GenericType = EGeneric.SpecializedClass;

                //Debug.WriteLine("特化クラス : {0}", reg_class.GetClassText(), "");
                PrjParser.SpecializedClassTable.Add(class_text, reg_class);
            }

            return reg_class;
        }

        public virtual void LineEnd() {
            GetToken(EKind.EOT);
        }

        public TUsing ReadUsing() {
            TUsing using1 = new TUsing();

            GetToken(EKind.using_);

            while (true) {
                TToken id = GetToken(EKind.Identifier);

                using1.Packages.Add(id.TextTkn);
                if(CurTkn.Kind != EKind.Dot) {
                    break;
                }
                GetToken(EKind.Dot);
            }

            LineEnd();

            return using1;
        }

        public TNamespace ReadNamespace() {
            GetToken(EKind.namespace_);

            TToken id = GetToken(EKind.Identifier);
            GetToken(EKind.LC);
            GetToken(EKind.EOT);

            return new TNamespace(id.TextTkn);
        }


        public TFunction ReadFunctionLine(TClass parent_class, TToken constructor_token, TClass constructor_class, bool is_static, TClass ret_type_prepend) {
            TToken fnc_name;
            
            if(constructor_class != null) {
                fnc_name = constructor_token;
            }
            else {

                if (CurTkn.Kind == EKind.operator_) {

                    GetToken(EKind.operator_);
                    fnc_name = GetToken(EKind.Undefined);
                    if (fnc_name.TokenType != ETokenType.Symbol) {
                        throw new TParseException();
                    }

                }
                else {

                    fnc_name = GetToken(EKind.Identifier);
                }
            }

            GetToken(EKind.LP);

            List<TVariable> vars = new List<TVariable>();
            while (CurTkn.Kind != EKind.RP) {
                TVariable var1 = ReadArgVariable();
                vars.Add(var1);

                if (CurTkn.Kind != EKind.Comma) {

                    break;
                }

                GetToken(EKind.Comma);
            }

            GetToken(EKind.RP);

            TClass ret_type = ret_type_prepend;
            TApply base_app = null;

            if(CurTkn.Kind == EKind.Colon) {

                GetToken(EKind.Colon);

                if(ret_type_prepend == null) {

                    ret_type = ReadType(parent_class, false);
                }
                else if(constructor_class != null) {

                    if(CurTkn.Kind == EKind.base_) {

                        base_app = PrimaryExpression() as TApply;
                    }
                    else {
                        throw new TParseException();
                    }
                }
            }

            if(CurTkn.Kind == EKind.SemiColon) {

                GetToken(EKind.SemiColon);
            }
            else {

                LCopt();
            }

            GetToken(EKind.EOT);

            return new TFunction(is_static, fnc_name, vars.ToArray(), ret_type, base_app);
        }

        public virtual TVariable ReadArgVariable() {
            switch (CurTkn.Kind) {
            case EKind.ref_:
                GetToken(EKind.ref_);
                break;

            case EKind.out_:
                GetToken(EKind.out_);
                break;

            case EKind.params_:
                GetToken(EKind.params_);
                break;

            }

            TToken id = GetToken(EKind.Identifier);

            TClass type = null;
            if (CurTkn.Kind == EKind.Colon) {

                GetToken(EKind.Colon);

                type = ReadType(null, false);
            }

            return new TVariable(id, type, null);
        }

        public TVariable ReadVariable(TClass type_prepend) {
            TToken id = GetToken(EKind.Identifier);

            TClass type = null;
            if (type_prepend != null) {

                type = type_prepend;
            }
            else { 

                if (CurTkn.Kind == EKind.Colon) {

                    GetToken(EKind.Colon);

                    type = ReadType(null, false);
                }
            }

            TTerm init = null;
            if (CurTkn.Kind == EKind.Assign) {

                GetToken(EKind.Assign);

                init = Expression();
            }

            return new TVariable(id, type, init);
        }

        public virtual TVariableDeclaration ReadVariableDeclarationLine(TClass type_prepend, bool in_for) {
            TVariableDeclaration var_decl = new TVariableDeclaration();

            if(type_prepend == null) {

                GetToken(EKind.var_);
            }

            while (true) {
                TVariable var1 = ReadVariable(type_prepend);

                var_decl.Variables.Add(var1);

                if(CurTkn.Kind != EKind.Comma) {

                    break;
                }

                GetToken(EKind.Comma);
            }

            if(!in_for && TokenList[TokenList.Length - 1].Kind != EKind.LC) {

                LineEnd();
            }

            return var_decl;
        }

        public TIfBlock ReadIfLine() {
            TIfBlock if_block = new TIfBlock();

            GetToken(EKind.if_);
            LPopt();

            if_block.ConditionIf = Expression();
            RPopt();
            LCopt();

            GetToken(EKind.EOT);
            return if_block;
        }

        public TIfBlock ReadElseLine() {
            TIfBlock if_block = new TIfBlock();

            GetToken(EKind.else_);

            if(CurTkn.Kind == EKind.if_) {

                GetToken(EKind.if_);
                LPopt();
                if_block.ConditionIf = Expression();
                RPopt();
            }
            LCopt();
            GetToken(EKind.EOT);

            return if_block;
        }

        public TSwitch ReadSwitchLine() {
            TSwitch switch1 = new TSwitch();

            GetToken(EKind.switch_);

            LPopt();
            switch1.TermSwitch = Expression();
            RPopt();
            LCopt();
            GetToken(EKind.EOT);

            return switch1;
        }

        public TCase ReadCaseLine() {
            TCase case1 = new TCase();

            TToken tkn = GetToken(EKind.case_);

            List<TTerm> expr_list = ExpressionList();
            case1.TermsCase.AddRange(expr_list);

            Colonopt();
            if(CurTkn.Kind == EKind.LC) {
                GetToken(EKind.LC);
            }
            GetToken(EKind.EOT);
            return case1;
        }

        public TWhile ReadWhileLine() {
            TWhile while1 = new TWhile();

            GetToken(EKind.while_);

            LPopt();
            while1.WhileCondition = Expression();
            RPopt();

            LCopt();
            GetToken(EKind.EOT);
            return while1;
        }

        public TForEach ReadForEachLine() {
            TForEach for1 = new TForEach();

            GetToken(EKind.foreach_);
            LPopt();

            if(CurTkn.Kind == EKind.ClassName) {

                for1.LoopVariable = ReadArgVariable();
            }
            else {

                TToken id = GetToken(EKind.Identifier);
                for1.LoopVariable = new TVariable(id);
            }

            GetToken(EKind.in_);

            for1.ListFor = Expression();

            RPopt();
            LCopt();
            GetToken(EKind.EOT);

            return for1;
        }

        public TFor ReadForLine() {
            TFor for1 = new TFor();

            GetToken(EKind.for_);
            GetToken(EKind.LP);

            if (CurTkn.Kind != EKind.SemiColon) {

                if (CurTkn.Kind == EKind.ClassName) {
                    TClass tp = ReadType(null, false);

                    for1.InitStatement = ReadVariableDeclarationLine(tp, true);
                    for1.LoopVariable = (for1.InitStatement as TVariableDeclaration).Variables[0];
                }
                else {

                    for1.InitStatement = ReadAssignmentCallLine(true) as TStatement;
                }
            }

            GetToken(EKind.SemiColon);

            if (CurTkn.Kind != EKind.SemiColon) {

                for1.ConditionFor = Expression();
            }

            GetToken(EKind.SemiColon);

            if(CurTkn.Kind != EKind.RP) {

                for1.PostStatement = ReadAssignmentCallLine(true) as TStatement;
            }

            GetToken(EKind.RP);
            GetToken2(EKind.LC, EKind.SemiColon);
            GetToken(EKind.EOT);

            return for1;
        }

        public TTry ReadTryLine() {
            GetToken(EKind.try_);
            LCopt();
            GetToken(EKind.EOT);
            return new TTry();
        }

        public TCatch ReadCatchLine() {
            GetToken(EKind.catch_);
            LPopt();

            TClass tp = ReadType(null, false);

            string name = "";
            if(CurTkn.Kind == EKind.Identifier) {
                name = GetToken(EKind.Identifier).TextTkn;
            }

            RPopt();
            LCopt();
            GetToken(EKind.EOT);

            return new TCatch(new TVariable(name, tp));
        }

        public TJump ReadJumpLine() {
            TToken tkn = GetToken(EKind.Undefined);

            TJump jump = new TJump(tkn.Kind);

            switch (tkn.Kind) {
            case EKind.yield_:
                if(CurTkn.Kind == EKind.return_) {

                    GetToken(EKind.return_);
                    jump.RetVal = Expression();
                }
                else if (CurTkn.Kind == EKind.break_) {

                    GetToken(EKind.break_);
                }
                else {
                    throw new TParseException();
                }
                break;

            case EKind.return_:
            case EKind.throw_:
                if(CurTkn.Kind != EKind.SemiColon && CurTkn.Kind != EKind.EOT) {

                    jump.RetVal = Expression();
                }
                break;

            case EKind.break_:                
                break;

            case EKind.goto_:
                GetToken(EKind.Identifier);
                break;
            }

            LineEnd();

            return jump;
        }

        public object ReadAssignmentCallLine(bool in_for) {
            TAssignment asn = null;

            TTerm t1 = Expression();

            switch (CurTkn.Kind) {
            case EKind.Assign:
            case EKind.AddEq:
            case EKind.SubEq:
            case EKind.DivEq:
            case EKind.ModEq:

                TToken opr = GetToken(EKind.Undefined);

                TTerm t2 = Expression();

                TApply app1 = new TApply(opr, t1, t2);

                asn = new TAssignment(app1);
                break;
            }

            if(!in_for) {

                if(CurTkn.Kind == EKind.Comma) {
                    GetToken(EKind.Comma);
                    GetToken(EKind.EOT);
                    return t1;
                }

                LineEnd();
            }

            if(asn != null) {
                return asn;
            }

            if (!(t1 is TApply)) {
                throw new TParseException();
            }

            return new TCall(t1 as TApply);
        }

        public int LineTopTokenIndex(TToken[] token_list) {
            for(int i = 0; i < token_list.Length; i++) {
                TToken tkn = token_list[i];
                switch (tkn.TokenType) {
                case ETokenType.BlockComment:
                case ETokenType.BlockCommentContinued:
                case ETokenType.LineComment:
                case ETokenType.White:
                    break;

                default:
                    return i;
                }
            }

            return -1;
        }

        public object ParseLine(TClass cls, TFunction parent_fnc, TStatement parent_stmt, int line_top_idx, TToken[] token_list) {
            TokenList = token_list;
            TokenPos = line_top_idx;

            if (TokenPos < TokenList.Length) {

                CurTkn = TokenList[TokenPos];
            }
            else {

                CurTkn = EOTToken;
            }

            if (TokenPos + 1 < TokenList.Length) {

                NextTkn = TokenList[TokenPos + 1];
            }
            else {

                NextTkn = EOTToken;
            }

            object line_obj = null;

            bool is_static = false;

            try {
                if(parent_stmt is TVariableDeclaration) {
                    TVariable var1 = (parent_stmt as TVariableDeclaration).Variables[0];

                    if(! (var1.InitValue is TNewApply)) {
                        throw new TParseException();
                    }

                    TTerm ele = Expression();

                    OptGetToken(EKind.Comma);

                    (var1.InitValue as TNewApply).InitList.Add(ele);

                    return ele;
                }

                while (true) {
                    switch (CurTkn.Kind) {
                    case EKind.public_:
                        GetToken(EKind.public_);
                        break;

                    case EKind.private_:
                        GetToken(EKind.private_);
                        break;

                    case EKind.abstract_:
                        GetToken(EKind.abstract_);
                        break;

                    case EKind.sealed_:
                        GetToken(EKind.sealed_);
                        break;

                    case EKind.partial_:
                        GetToken(EKind.partial_);
                        break;

                    case EKind.const_:
                        GetToken(EKind.const_);
                        break;

                    case EKind.static_:
                        GetToken(EKind.static_);
                        is_static = true;
                        break;

                    case EKind.virtual_:
                        GetToken(EKind.virtual_);
                        break;

                    case EKind.override_:
                        GetToken(EKind.override_);
                        break;

                    case EKind.async_:
                        GetToken(EKind.async_);
                        break;

                    case EKind.RC:
                        GetToken(EKind.RC);
                        if(CurTkn.Kind == EKind.SemiColon) {

                            GetToken(EKind.SemiColon);
                        }
                        GetToken(EKind.EOT);
                        return null;

                    case EKind.get_:
                        GetToken(EKind.get_);
                        GetToken(EKind.LC);
                        GetToken(EKind.EOT);
                        return null;

                    default:
                        goto start_l;
                    }
                }
                start_l:

                switch (CurTkn.Kind) {
                case EKind.using_:
                    return ReadUsing();

                case EKind.namespace_:
                    return ReadNamespace();

                case EKind.enum_:
                    return ReadEnumLine();

                case EKind.class_:
                case EKind.struct_:
                    return ReadClassLine();

                case EKind.delegate_: {

                        GetToken(EKind.delegate_);
                        TClass tp = ReadType(null, false);
                        TFunction fnc = ReadFunctionLine(cls, null, null, false, tp);
                        return new TClass(fnc);
                    }

                case EKind.var_:
                    return ReadVariableDeclarationLine(null, false);

                case EKind.if_:
                    return ReadIfLine();

                case EKind.else_:
                    return ReadElseLine();

                case EKind.switch_:
                    return ReadSwitchLine();

                case EKind.case_:
                    return ReadCaseLine();

                case EKind.default_:
                    return new TCase();

                case EKind.while_:
                    return ReadWhileLine();

                case EKind.for_:
                    return ReadForLine();

                case EKind.foreach_:
                    return ReadForEachLine();

                case EKind.try_:
                    return ReadTryLine();

                case EKind.catch_:
                    return ReadCatchLine();

                case EKind.return_:
                case EKind.yield_:
                case EKind.throw_:
                case EKind.break_:
                case EKind.goto_:
                    return ReadJumpLine();

                case EKind.ClassName: {
                        TToken class_token = CurTkn;
                        TClass tp = ReadType(null, false);

                        if (CurTkn.Kind == EKind.LP) {

                            return ReadFunctionLine(cls, class_token, tp, is_static, tp);
                        }

                        if (CurTkn.Kind != EKind.Identifier) {

                            LookaheadClass = tp;
                            return ReadAssignmentCallLine(false);
                        }

                        if (NextTkn.Kind == EKind.LP) {

                            return ReadFunctionLine(cls, null, null, is_static, tp);
                        }
                        else {

                            if (parent_fnc == null) {

                                return ReadFieldLine(cls, is_static, tp);
                            }
                            else {

                                return ReadVariableDeclarationLine(tp, false);
                            }
                        }
                    }

                case EKind.Identifier:
                case EKind.base_:
                    if(cls != null && cls.KindClass == EClass.Enum) {

                        return ReadEnumFieldLine(cls);
                    }

                    if (NextTkn.Kind == EKind.Colon) {
                        if(TokenPos + 2 < TokenList.Length) {

                            return ReadFieldLine(cls, is_static, null);
                        }
                        else {

                            TToken id = GetToken(EKind.Identifier);
                            GetToken(EKind.Colon);
                            return new TLabelStatement(id);
                        }

                    }
                    else if (parent_fnc == null) {

                        return ReadFunctionLine(cls, null, null, is_static, null);
                    }
                    else {

                        return ReadAssignmentCallLine(false);
                    }

                case EKind.this_:
                case EKind.await_:
                    return ReadAssignmentCallLine(false);

                case EKind.operator_:
                    return ReadFunctionLine(cls, null, null, is_static, null);

                case EKind.LB:
                    GetToken(EKind.LB);
                    TClass attr = ReadType(null, false);
                    GetToken(EKind.RB);
                    return new TAttribute(attr);

                default:
                    Debug.WriteLine("行頭 {0}", CurTkn.Kind);
                    break;
                }

                TTerm t1 = Expression();
            }
            catch (TParseException) {

            }

            return line_obj;
        }

        public void GetVariableClass(TSourceFile src, int current_line_idx, out List<TVariable> vars) {
            vars = new List<TVariable>();

            int min_indent = src.Lines[current_line_idx].Indent;

            for (int line_idx = current_line_idx; 0 <= line_idx; line_idx--) {
                TLine line = src.Lines[line_idx];

                if (line.ObjLine != null && line.Indent <= min_indent) {

                    if (line.Indent < min_indent) {

                        min_indent = line.Indent;
                    }

                    if (line.ObjLine is TVariableDeclaration) {

                        TVariableDeclaration var_decl = line.ObjLine as TVariableDeclaration;
                        vars.AddRange(var_decl.Variables);
                    }
                    else if (line.ObjLine is TFunction) {
                        TFunction fnc = line.ObjLine as TFunction;
                        vars.AddRange(fnc.ArgsFnc);
                    }
                    else if (line.ObjLine is TFor) {
                        TFor for1 = line.ObjLine as TFor;
                        vars.Add(for1.LoopVariable);
                    }
                    else if (line.ObjLine is TCatch) {
                        TCatch catch1 = line.ObjLine as TCatch;
                        vars.Add(catch1.CatchVariable);
                    }
                    else if (line.ObjLine is TClass) {

                        return;
                    }
                    foreach(TVariable x in vars) {
                        Debug.Assert(x != null);
                    }
                }
            }
        }

        public async void ParseFile(TSourceFile src) {
            while (Running) {
                await Task.Delay(1);
            }
            Running = true;
            Dirty = false;
            Debug.WriteLine("parse file : 開始 {0}", Path.GetFileName(src.PathSrc), "");

            Dictionary<string, int> dic = new Dictionary<string, int>();
            dic.Add("int", 0);
            dic.Add("float", 1);
            dic.Add("double", 2);
            dic.Add("char", 3);
            dic.Add("string", 4);
            dic.Add("bool", 5);
            dic.Add("void", 6);

            src.ClassesSrc.Clear();

            List<object> obj_stack = new List<object>();
            for(int line_idx = 0; line_idx < src.Lines.Count; line_idx++) {
                await Task.Delay(1);

                //Debug.WriteLine("parse file : {0} {1}", line_idx, Dirty);
                if (Dirty) {
                    Debug.WriteLine("parse file : 中断");
                    Running = false;
                    return;
                }

                TLine line = src.Lines[line_idx];

                line.Indent = -1;
                line.ObjLine = null;
                if (line.Tokens != null && line.Tokens.Length != 0) {

                    var v = from x in line.Tokens where x.TokenType != ETokenType.White select x;
                    if (v.Any()) {

                        TToken[] token_list = v.ToArray();
                        int line_top_idx = LineTopTokenIndex(token_list);

                        if (line_top_idx != -1) {

                            TToken line_top_token = token_list[line_top_idx];
                            line.Indent = line_top_token.StartPos;

                            switch (line_top_token.TokenType) {
                            case ETokenType.Undefined:
                            case ETokenType.VerbatimString:
                            case ETokenType.VerbatimStringContinued:
                            case ETokenType.LineComment:
                            case ETokenType.BlockComment:
                            case ETokenType.BlockCommentContinued:
                            case ETokenType.Error:
                                break;

                            default:

                                if (line.Indent < obj_stack.Count) {

                                    obj_stack.RemoveRange(line.Indent, obj_stack.Count - line.Indent);
                                }

                                TClass cls = null;
                                TFunction parent_fnc = null;
                                TBlockStatement parent_stmt = null;
                                List<object> obj_stack_rev = new List<object>(obj_stack);
                                obj_stack_rev.Reverse();

                                // スタックの中からクラスを探します。
                                var vcls = from x in obj_stack_rev where x is TClass select x as TClass;
                                if (vcls.Any()) {
                                    cls = vcls.First();
                                }
                                line.ClassLine = cls;

                                // スタックの中から関数を探します。
                                var vfnc = from x in obj_stack_rev where x is TFunction select x as TFunction;
                                if (vfnc.Any()) {
                                    parent_fnc = vfnc.First();
                                }

                                // スタックの中から最も内側の文を探します。
                                var vstmt = from x in obj_stack_rev where x is TBlockStatement select x as TBlockStatement;
                                if (vstmt.Any()) {
                                    parent_stmt = vstmt.First();
                                }

                                //Debug.Write(string.Format("行解析 {0}", line.TextLine));
                                object obj = ParseLine(cls, parent_fnc, parent_stmt, line_top_idx, token_list);
                                if (obj != null) {

                                    while(obj_stack.Count < line.Indent) {
                                        obj_stack.Add(null);
                                    }
                                    obj_stack.Add(obj);
                                    Debug.Assert(obj_stack.IndexOf(obj) == line.Indent);

                                    //StringWriter sw = new StringWriter();
                                    if (obj is TClass) {
                                        TClass class_def = obj as TClass;

                                        //ClassLineText(class_def, sw);
                                        src.ClassesSrc.Add(class_def);

                                        int sys_class;
                                        if(dic.TryGetValue(class_def.ClassName, out sys_class)) {
                                            switch (sys_class) {
                                            case 0:
                                                TProject.IntClass = class_def;
                                                break;
                                            case 1:
                                                TProject.FloatClass = class_def;
                                                break;
                                            case 2:
                                                TProject.DoubleClass = class_def;
                                                break;
                                            case 3:
                                                TProject.CharClass = class_def;
                                                break;
                                            case 4:
                                                TProject.StringClass = class_def;
                                                break;
                                            case 5:
                                                TProject.BoolClass = class_def;
                                                break;
                                            case 6:
                                                TProject.VoidClass = class_def;
                                                break;
                                            }
                                        }
                                    }
                                    else if (obj is TVariable) {

                                        if(cls != null) {

                                            if (obj is TField) {
                                                TField fld = obj as TField;

                                                cls.Fields.Add(fld);
                                            }
                                            else if (obj is TFunction) {
                                                TFunction fnc = obj as TFunction;

                                                cls.Functions.Add(fnc);
                                            }
                                        }
                                        //VariableText(obj as TVariable, sw);
                                    }
                                    else if (obj is TTerm) {

                                        //TermText(obj as TTerm, sw);
                                    }
                                    else if (obj is TStatement) {
                                        TStatement stmt = obj as TStatement;

                                        if(parent_stmt != null) {

                                            parent_stmt.StatementsBlc.Add(stmt);
                                        }
                                        else if(parent_fnc != null) {

                                            parent_fnc.BlockFnc.StatementsBlc.Add(stmt);
                                        }
                                        //StatementText(stmt, sw, 0);
                                    }

                                    //Debug.WriteLine(sw.ToString());

                                }

                                line.ObjLine = obj;
                                break;
                            }
                        }
                    }
                }
            }

/*
            Debug.WriteLine("名前解決 : 開始");
            for (int line_idx = 0; line_idx < src.Lines.Count; line_idx++) {
                await Task.Delay(1);
                //Debug.WriteLine("名前解決 : {0} {1}", line_idx, Dirty);
                if (Dirty) {
                    Debug.WriteLine("名前解決 : 中断");
                    Running = false;
                    return;
                }

                TLine line = src.Lines[line_idx];

                if(line.ObjLine is TStatement) {
                    TStatement stmt = line.ObjLine as TStatement;

                    List<TVariable> vars;
                    GetVariableClass(src, line_idx, out vars);

                    // 名前解決のエラーをクリアします。
                    var name_err_tkns = from x in line.Tokens where x.ErrorTkn is TResolveNameException select x;
                    foreach (TToken name_err_tkn in name_err_tkns) {
                        name_err_tkn.ErrorTkn = null;
                    }

                    try {
                        stmt.ResolveName(line.ClassLine, vars);
                    }
                    catch (TResolveNameException) {
                    }
                }
            }
            Debug.WriteLine("名前解決 : 終了");
*/

            foreach(MyEditor editor in src.Editors) {
                editor.InvalidateCanvas();
            }

            Running = false;
        }

        public TToken GetToken(EKind type) {

            if(type != EKind.Undefined && type != CurTkn.Kind) {

                throw new TParseException();
            }

            TToken tkn = CurTkn;

            while (true) {

                TokenPos++;
                if (TokenPos < TokenList.Length) {

                    CurTkn = TokenList[TokenPos];

                    switch (CurTkn.TokenType) {
                    case ETokenType.White:
                    case ETokenType.BlockComment:
                    case ETokenType.BlockCommentContinued:
                    case ETokenType.LineComment:
                        break;

                    default:
                        goto while_end;
                    }
                }
                else {

                    CurTkn = EOTToken;
                    break;
                }
            }
            while_end:

            if (TokenPos + 1 < TokenList.Length) {

                NextTkn = TokenList[TokenPos + 1];
            }
            else {

                NextTkn = EOTToken;
            }

            return tkn;
        }

        public TToken GetToken2(EKind kind1, EKind kind2) {
            if (CurTkn.Kind != kind1 && CurTkn.Kind != kind2) {

                throw new TParseException();
            }
            return GetToken(EKind.Undefined);
        }

        public void OptGetToken(EKind kind) {
            if (CurTkn.Kind == kind) {

                GetToken(kind);
            }
        }

        public TFrom FromExpression() {
            TFrom from1 = new TFrom();

            GetToken(EKind.from_);

            TToken id1 = GetToken(EKind.Identifier);
            from1.VarQry = new TVariable(id1);

            GetToken(EKind.in_);
            from1.SeqQry = Expression();

            if (CurTkn.Kind == EKind.where_) {
                GetToken(EKind.where_);
                from1.CndQry = Expression();
            }

            if(CurTkn.Kind == EKind.select_) {
                GetToken(EKind.select_);
                from1.SelFrom = Expression();
            }

            if(CurTkn.Kind == EKind.from_) {
                from1.InnerFrom = FromExpression();
            }

            return from1;
        }

        public TTerm PrimaryExpression() {
            TToken id;
            TTerm[] args;
            TClass cls;
            TTerm term;

            if(LookaheadClass != null) {
                TReference ref_class = new TReference(LookaheadClass);
                LookaheadClass = null;
                return ref_class;
            }

            switch (CurTkn.Kind) {
            case EKind.Identifier:
            case EKind.this_:
            case EKind.true_:
            case EKind.false_:
            case EKind.null_:
            case EKind.base_:
                id = GetToken(EKind.Undefined);

                if (CurTkn.Kind == EKind.LP) {
                    GetToken(EKind.LP);

                    TTerm[] expr_list = ExpressionList().ToArray();

                    GetToken(EKind.RP);

                    return new TApply(id, expr_list);
                }
                else if(CurTkn.Kind == EKind.Lambda) {
                    GetToken(EKind.Lambda);

                    TTerm ret = Expression();
                    TFunction fnc = new TFunction(id.TextTkn, ret);

                    return new TReference(fnc);
                }
                else {

                    return new TReference(id);
                }

            case EKind.LP:
                GetToken(EKind.LP);
                if(CurTkn.Kind == EKind.ClassName) {

                    cls = ReadType(null, false);
                    if(CurTkn.Kind == EKind.RP) {

                        GetToken(EKind.RP);
                        term = Expression();
                        term.CastType = cls;

                        return term;
                    }
                    else {

                        LookaheadClass = cls;
                    }
                }

                term = Expression();

                GetToken(EKind.RP);

                term.WithParenthesis = true;
                return term;


            case EKind.NumberLiteral:
            case EKind.StringLiteral:
            case EKind.CharLiteral:
                TToken tkn = GetToken(EKind.Undefined);

                return new TLiteral(tkn);

            case EKind.new_:
                TToken new_tkn = GetToken(EKind.new_);

                cls = ReadType(null, true);
                if(CurTkn.Kind == EKind.LP) {

                    GetToken(EKind.LP);

                    args = ExpressionList().ToArray();

                    GetToken(EKind.RP);

                    return new TNewApply(EKind.NewInstance, new_tkn, cls, args, null);
                }
                else if (CurTkn.Kind == EKind.LB) {

                    GetToken(EKind.LB);

                    args = ExpressionList().ToArray();

                    GetToken(EKind.RB);

                    List<TTerm> init = null;
                    if (CurTkn.Kind == EKind.LC) {

                        GetToken(EKind.LC);

                        if(CurTkn.Kind != EKind.EOT) {

                            init = ExpressionList();
                            GetToken(EKind.RC);
                        }
                    }

                    return new TNewApply(EKind.NewArray, new_tkn, cls, args, init);
                }
                else {
                    throw new TParseException();
                }

            case EKind.ClassName:
                id = CurTkn;
                cls = ReadType(null, false);

                //!!!!!!!!!! idとclsは違う !!!!!!!!!!
                return new TReference(cls);

            case EKind.from_:
                return FromExpression();

            case EKind.await_:
                TToken opr = GetToken(EKind.await_);
                term = Expression();
                return new TApply(opr, term);
            }

            throw new TParseException();
        }

        public TTerm DotIndexExpression() {
            TTerm t1 = PrimaryExpression();

            while(CurTkn.Kind == EKind.Dot || CurTkn.Kind == EKind.LB) {
                if(CurTkn.Kind == EKind.Dot) {

                    GetToken(EKind.Dot);

                    TToken id = GetToken2(EKind.Identifier, EKind.ClassName);

                    if (CurTkn.Kind == EKind.LP) {
                        GetToken(EKind.LP);

                        TTerm[] args = ExpressionList().ToArray();

                        GetToken(EKind.RP);

                        t1 = new TDotApply(t1, id, args);
                    }
                    else {

                        t1 = new TDotReference(t1, id);
                    }
                }
                else {

                    TToken lb = GetToken(EKind.LB);

                    TTerm[] args = ExpressionList().ToArray();

                    GetToken(EKind.RB);

                    t1 = new TApply(lb, t1, args);
                }
            }

            return t1;
        }

        public TTerm PostIncDecExpression() {
            TTerm t1 = DotIndexExpression();

            if (CurTkn.Kind == EKind.Inc || CurTkn.Kind == EKind.Dec) {
                TToken opr = GetToken(EKind.Undefined);

                return new TApply(opr, t1);
            }
            else {

                return t1;
            }
        }

        public TTerm UnaryExpression() {
            if (CurTkn.Kind == EKind.Add || CurTkn.Kind == EKind.Sub) {
                TToken opr = GetToken(EKind.Undefined);

                TTerm t1 = PostIncDecExpression();

                return new TApply(opr, t1);
            }
            else {

                return PostIncDecExpression();
            }
        }

        public TTerm MultiplicativeExpression() {
            TTerm t1 = UnaryExpression();

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.Mul:
                case EKind.Div:
                case EKind.Mod:

                    TToken opr = GetToken(EKind.Undefined);
                    TTerm t2 = UnaryExpression();

                    t1 = new TApply(opr, t1, t2);
                    break;

                default:
                    return t1;
                }
            }
        }

        public TTerm AdditiveExpression() {
            TTerm t1 = MultiplicativeExpression();

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.Add:
                case EKind.Sub:

                    TToken opr = GetToken(EKind.Undefined);
                    TTerm t2 = MultiplicativeExpression();

                    t1 = new TApply(opr, t1, t2);
                    break;

                default:
                    return t1;
                }
            }
        }

        public TTerm RelationalExpression() {
            TTerm t1 = AdditiveExpression();

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.Eq:
                case EKind.NE:
                case EKind.LT:
                case EKind.LE:
                case EKind.GT:
                case EKind.GE:
                case EKind.is_:

                    TToken opr = GetToken(EKind.Undefined);
                    TTerm t2 = AdditiveExpression();

                    t1 = new TApply(opr, t1, t2);
                    break;

                case EKind.as_:
                    GetToken(EKind.as_);
                    t1.CastType = ReadType(null, false);
                    break;

                default:
                    return t1;
                }
            }
        }

        public TTerm NotExpression() {
            if(CurTkn.Kind == EKind.Not_) {

                TToken not_tkn = GetToken(EKind.Not_);
                TTerm t1 = RelationalExpression();

                return new TApply(not_tkn, t1);
            }

            return RelationalExpression();
        }

        public TTerm BitExpression() {
            TTerm t1 = NotExpression();

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.Hat:
                case EKind.Anp:
                case EKind.BitOR:

                    TToken opr = GetToken(EKind.Undefined);
                    TTerm t2 = NotExpression();

                    t1 = new TApply(opr, t1, t2);
                    break;

                default:
                    return t1;
                }
            }
        }

        public TTerm AndExpression() {
            TTerm t1 = BitExpression();

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.And_:

                    TToken opr = GetToken(EKind.Undefined);
                    TTerm t2 = BitExpression();

                    t1 = new TApply(opr, t1, t2);
                    break;

                default:
                    return t1;
                }
            }
        }

        public TTerm OrExpression() {
            TTerm t1 = AndExpression();

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.Or_:

                    TToken opr = GetToken(EKind.Undefined);
                    TTerm t2 = AndExpression();

                    t1 = new TApply(opr, t1, t2);
                    break;

                default:
                    return t1;
                }
            }
        }

        public TTerm ConditionalExpression() {
            TTerm t1 = OrExpression();

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.Question:
                    TToken opr = GetToken(EKind.Question);
                    TTerm t2 = OrExpression();

                    GetToken(EKind.Colon);

                    TTerm t3 = OrExpression();

                    t1 = new TApply(opr, t1, t2, t3);
                    break;

                default:
                    return t1;
                }
            }
        }

        public List<TTerm> ExpressionList() {
            List<TTerm> expr_list = new List<TTerm>();

            if (CurTkn.Kind == EKind.RP || CurTkn.Kind == EKind.RB || CurTkn.Kind == EKind.RC) {
                return expr_list;
            }

            while (true) {
                switch (CurTkn.Kind) {
                case EKind.out_:
                    GetToken(EKind.out_);
                    break;

                case EKind.ref_:
                    GetToken(EKind.ref_);
                    break;
                }
                TTerm t1 = Expression();
                expr_list.Add(t1);

                if(CurTkn.Kind == EKind.Comma) {

                    GetToken(EKind.Comma);
                }
                else {
                    
                    return expr_list;
                }
            }
        }

        public TTerm Expression() {
            return ConditionalExpression();
        }


        public void ClassLineText(TClass cls, StringWriter sw) {
            sw.Write("class {0}", cls.ClassName);

            for (int i = 0; i < cls.SuperClasses.Count; i++) {
                if (i == 0) {

                    sw.Write(" : ");
                }
                else {

                    sw.Write(" , ");
                }
                sw.Write(cls.SuperClasses[i].ClassName);
            }

            sw.WriteLine();
        }

        public void ClassText(TClass cls, StringWriter sw) {
            sw.Write(cls.GetClassText());
        }

        public void VariableText(TVariable var1, StringWriter sw) {
            sw.Write(var1.NameVar);

            if (var1.TypeVar != null) {

                sw.Write(" : ");
                ClassText(var1.TypeVar, sw);
            }

            if (var1.InitValue != null) {

                sw.Write(" = ");
                TermText(var1.InitValue, sw);
            }
        }

        public void ArgsText(TApply app, StringWriter sw) {
            foreach (TTerm trm in app.Args) {
                if (trm != app.Args[0]) {

                    sw.Write(", ");
                }

                TermText(trm, sw);
            }
        }

        public void TermText(TTerm term, StringWriter sw) {
            if (term is TLiteral) {
                TLiteral lit = term as TLiteral;

                sw.Write(lit.TokenTrm.TextTkn);
            }
            else if (term is TReference) {
                TReference ref1 = term as TReference;

                if (ref1 is TDotReference) {

                    TermText((ref1 as TDotReference).DotRef, sw);
                    sw.Write(".");
                }

                sw.Write(ref1.NameRef);
            }
            else if (term is TApply) {
                TApply app = term as TApply;

                if (app is TDotApply) {

                    TermText((app as TDotApply).DotApp, sw);
                    sw.Write(".");
                }

                switch (app.KindApp) {
                case EKind.FunctionApply:
                    TermText(app.FunctionApp, sw);
                    sw.Write("(");
                    ArgsText(app, sw);
                    sw.Write(")");
                    break;

                case EKind.Index:
                    TermText(app.FunctionApp, sw);
                    sw.Write("[");
                    ArgsText(app, sw);
                    sw.Write("]");
                    break;

                case EKind.NewInstance:
                    sw.Write("new ");
                    ClassText((app as TNewApply).ClassApp, sw);
                    sw.Write("(");
                    ArgsText(app, sw);
                    sw.Write(")");
                    break;

                case EKind.NewArray:
                    sw.Write("new ");
                    ClassText((app as TNewApply).ClassApp, sw);
                    sw.Write("[");
                    ArgsText(app, sw);
                    sw.Write("]");
                    break;

                case EKind.base_:
                    sw.Write("base.");
                    TermText(app.FunctionApp, sw);
                    sw.Write("(");
                    ArgsText(app, sw);
                    sw.Write(")");
                    break;

                default:
                    switch (app.Args.Length) {
                    case 1:
                        sw.Write("{0} ", KindString[app.KindApp]);
                        TermText(app.Args[0], sw);
                        break;

                    case 2:
                        TermText(app.Args[0], sw);
                        sw.Write(" {0} ", KindString[app.KindApp]);
                        TermText(app.Args[1], sw);
                        break;

                    default:
                        Debug.Assert(false);
                        break;
                    }
                    break;
                }

            }
            else if (term is TQuery) {
                TQuery qry = term as TQuery;

                if (term is TFrom) {
                    TFrom from1 = term as TFrom;

                }
                if (term is TAggregate) {
                    TAggregate aggr = term as TAggregate;

                }
                else {
                    Debug.Assert(false);
                }
            }
            else {
                Debug.Assert(false);
            }
        }

        public void StatementText(TStatement stmt, StringWriter sw, int nest) {
            if (stmt is TVariableDeclaration) {
                TVariableDeclaration var_decl = stmt as TVariableDeclaration;

                sw.Write("{0}var ", Tab(nest));
                foreach (TVariable var1 in var_decl.Variables) {
                    if (var1 != var_decl.Variables[0]) {

                        sw.Write(", ");
                    }

                    VariableText(var1, sw);
                }
                sw.WriteLine();
            }
            else if (stmt is TAssignment) {
                TAssignment asn = stmt as TAssignment;

                sw.Write("{0}", Tab(nest));
                TermText(asn.RelAsn, sw);
                sw.WriteLine();
            }
            else if (stmt is TCall) {
                TCall call1 = stmt as TCall;

                sw.Write("{0}", Tab(nest));
                TermText(call1.AppCall, sw);
                sw.WriteLine();
            }
            else if (stmt is TJump) {
                TJump jmp = stmt as TJump;
            }
            else if (stmt is TBlockStatement) {
                TBlockStatement blc_stmt = stmt as TBlockStatement;

                if (stmt is TBlock) {
                    TBlock block = stmt as TBlock;
                }
                else if (stmt is TIfBlock) {
                    TIfBlock if_block = stmt as TIfBlock;

                    sw.Write("{0}if ", Tab(nest));
                    TermText(if_block.ConditionIf, sw);
                    sw.WriteLine();
                }
                else if (stmt is TCase) {
                    TCase cas = stmt as TCase;

                    sw.Write("{0}switch ", Tab(nest));
                    foreach(TTerm trm in cas.TermsCase) {
                        if(trm != cas.TermsCase[0]) {
                            // 最初でない場合

                            sw.Write(", ");
                            TermText(trm, sw);
                        }
                    }
                    sw.WriteLine();
                }
                else if (stmt is TSwitch) {
                    TSwitch swt = stmt as TSwitch;

                    sw.Write("{0}switch ", Tab(nest));
                    TermText(swt.TermSwitch, sw);
                    sw.WriteLine();

                    foreach (TCase cas in swt.Cases) {
                        StatementText(cas, sw, nest);
                    }
                }
                else if (stmt is TForEach) {
                    TForEach for1 = stmt as TForEach;

                    sw.Write("{0}foreach ", Tab(nest));
                    VariableText(for1.LoopVariable, sw);
                    sw.Write(" in ");
                    TermText(for1.ListFor, sw);
                    sw.WriteLine();
                }
                else if (stmt is TFor) {
                    TFor for1 = stmt as TFor;

                    sw.Write("{0}for(", Tab(nest));
                    if(for1.InitStatement != null) {

                        StatementText(for1.InitStatement, sw, 0);
                    }
                    sw.Write(" ; ");

                    if(for1.ConditionFor != null) {

                        TermText(for1.ConditionFor, sw);
                    }
                    sw.Write(" ; ");

                    if (for1.PostStatement != null) {

                        StatementText(for1.PostStatement, sw, 0);
                    }
                    sw.Write("){");
                    sw.WriteLine();
                }
                else if (stmt is TWhile) {
                    TWhile while1 = stmt as TWhile;

                    sw.Write("{0}while ", Tab(nest));
                    TermText(while1.WhileCondition, sw);
                    sw.WriteLine();
                }
                else if (stmt is TTry) {
                    TTry try1 = stmt as TTry;

                    sw.Write("{0}try ", Tab(nest));
                    sw.WriteLine();
                }
                else if (stmt is TCatch) {
                    TCatch catch1 = stmt as TCatch;

                    sw.Write("{0}catch ", Tab(nest));
                    VariableText(catch1.CatchVariable, sw);
                    sw.WriteLine();
                }
                else {
                    Debug.Assert(false);
                }

                foreach (TStatement stmt1 in blc_stmt.StatementsBlc) {
                    StatementText(stmt1, sw, nest + 1);
                }
            }
            else {
                Debug.Assert(false);
            }
        }

        public void SourceFileText(TSourceFile src, StringWriter sw) {
            foreach (TClass cls in src.ClassesSrc) {
                ClassLineText(cls, sw);

                foreach (TField fld in cls.Fields) {
                    sw.Write("\t");
                    VariableText(fld, sw);
                    sw.WriteLine();
                }

                foreach (TFunction fnc in cls.Functions) {
                    if(fnc.TokenVar.TokenType == ETokenType.Symbol) {

                        sw.Write("{0}operator {1}", Tab(0), fnc.NameVar);
                    }
                    else {

                        sw.Write("{0}{1}", Tab(1), fnc.NameVar);
                    }

                    sw.Write("(");
                    foreach (TVariable var1 in fnc.ArgsFnc) {
                        if (var1 != fnc.ArgsFnc[0]) {
                            sw.Write(", ");
                        }

                        VariableText(var1, sw);
                    }
                    sw.Write(")");

                    if (fnc.TypeVar != null) {

                        sw.Write(" : ");
                        ClassText(fnc.TypeVar, sw);
                    }
                    sw.WriteLine();

                    StatementText(fnc.BlockFnc, sw, 1);
                }
            }
        }
    }

    public class TToken {
        public ETokenType TokenType;
        public EKind Kind;
        public string TextTkn;
        public int StartPos;
        public int EndPos;
        public Exception ErrorTkn;

        public TToken(ETokenType token_type, EKind kind, string txt, int start_pos, int end_pos) {
            TokenType = token_type;
            Kind = kind;
            TextTkn = txt;
            StartPos = start_pos;
            EndPos = end_pos;
        }
    }

    public class TLine {
        public int Indent;
        public string TextLine;
        public TToken[] Tokens;
        public object ObjLine;
        public TClass ClassLine;
    }

    public class TParseException : Exception {
        public TParseException() {
        }

        public TParseException(string msg) {
            Debug.WriteLine(msg);
        }
    }

    public class TResolveNameException : Exception {
        public TResolveNameException(TToken tkn) {
            tkn.ErrorTkn = this;
        }

        public TResolveNameException(TReference ref1) {
            if(ref1.TokenTrm != null) {
                ref1.TokenTrm.ErrorTkn = this;
            }
        }
    }
}