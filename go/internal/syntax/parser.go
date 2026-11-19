package syntax

import "github.com/devlooped/jqlite/go/internal/values"

// MaxNesting is the nesting limit of spec §11.
const MaxNesting = 1000

// UpdateVariable is the variable that the rewriting of L op= R binds; '*' cannot appear in a
// source name.
const UpdateVariable = "*update"

// LocationVariable is $__loc__, the location object of §7.5: the resolver replaces it; no
// pattern, parameter or import binds it.
const LocationVariable = "__loc__"

type assoc int

const (
	assocLeft assoc = iota
	assocRight
	assocNone
)

// binaryLevel gives the binary operators (spec §3.1, levels 4 to 10) with their level, higher
// binding tighter, and associativity; level 0 for any other token. Levels 1 to 3 (|, ,, as) are
// parsed by parsePipe, levels 11 to 13 (unary minus, suffixes, terms) by parseExpression.
func binaryLevel(kind TokenKind) (int, assoc) {
	switch kind {
	case TokenAlt:
		return 4, assocRight
	case TokenAssign, TokenPipeAssign, TokenPlusAssign, TokenMinusAssign, TokenStarAssign,
		TokenSlashAssign, TokenPercentAssign, TokenAltAssign:
		return 5, assocNone
	case TokenOr:
		return 6, assocLeft
	case TokenAnd:
		return 7, assocLeft
	case TokenEqual, TokenNotEqual, TokenLess, TokenLessEqual, TokenGreater, TokenGreaterEqual:
		return 8, assocNone
	case TokenPlus, TokenMinus:
		return 9, assocLeft
	case TokenStar, TokenSlash, TokenPercent:
		return 10, assocLeft
	}
	return 0, assocLeft
}

// Parser is the recursive descent parser of jqlite (spec §3) over the tokens of the Lexer. It
// never panics for bad input: it gives the Program or one SyntaxError.
//
// Rewritten by the parser (spec §3.3), so the AST has one form for each:
//   - .. is the call recurse (resolved like any call: a user recurse/0 shadows it, as in jq).
//   - E? and try E are Try without a handler (which discards the error).
//   - .name, ."s", T.name, T."s" are Index with a literal or template key.
//   - elif nests If; a missing else is .; a foreach without extract has ..
//   - {$x} is {"x": $x}. The string shorthands ({a}, {"s"}, {@f "s"}) keep a nil value.
//   - L = R is _assign(L; R), L |= R is _modify(L; R), L op= R is R as $v | _modify(L; . op $v)
//     (//= likewise with //), where $v is UpdateVariable, a name no program can write.
//   - A string or @f "s" without interpolation is a Literal (a format applies to interpolations
//     only); parentheses leave no node.
//
// Where jq 1.8.2 parses differently from the EBNF of spec §3.2, this parser follows jq: def and
// label start a pipe or comma item only, never an operand (1 + def f: 1; f fails); object values
// are Alt ('|' Alt)* without as; object constructions accept a trailing comma; .[:] is an error; a
// definition name may be qualified (def a::b:); module metadata constants have no negative numbers.
//
// Nesting (brackets, interpolations, if, reduce, foreach, try, def) is limited to MaxNesting.
// Sequences do not recurse per element: pipes, commas, bindings and leading definitions are folded
// from a list (parsePipe), binary operators are parsed with an operator stack (parseExpression),
// and runs of unary minus and of elif are loops.
type Parser struct {
	tokens []*Token
	// The token at next, kept in a field: reading it is the commonest operation of the parser.
	current *Token
	next    int
	depth   int
	err     *SyntaxError
}

func newParser(source string) *Parser {
	tokens := Tokenize(source)
	return &Parser{tokens: tokens, current: tokens[0]}
}

// TryParse parses a program (spec §3.2, rule Program).
func TryParse(source string) (*Program, *SyntaxError) { return newParser(source).run(false) }

// TryParseModule parses a module file (spec §3.2, rule ModuleBody): directives and definitions only.
func TryParseModule(source string) (*Program, *SyntaxError) { return newParser(source).run(true) }

func (p *Parser) run(module bool) (*Program, *SyntaxError) {
	program := p.parseProgram(module)
	if program == nil {
		return nil, p.err
	}
	return program, nil
}

func (p *Parser) peek() *Token { return p.tokens[min(p.next+1, len(p.tokens)-1)] }

func (p *Parser) advance() {
	if p.next < len(p.tokens)-1 {
		p.next++
		p.current = p.tokens[p.next]
	}
}

func pos(token *Token) Position { return Position{token.Line, token.Column} }

func isLocation(token *Token) bool {
	return token.Kind == TokenVariable && token.Text == LocationVariable
}

// parseProgram: Program = ModuleDecl? Import* FuncDef* Pipe?; ModuleBody = ModuleDecl? Import* FuncDef*
func (p *Parser) parseProgram(module bool) *Program {
	var metadata *values.Value
	if p.current.Kind == TokenModule {
		p.advance()
		value := p.parseMetadata()
		if value == nil || !p.expect(TokenSemicolon, "';'") {
			return nil
		}
		metadata = value
	}

	var imports []*Import
	for p.current.Kind == TokenImport || p.current.Kind == TokenInclude {
		imp := p.parseImport()
		if imp == nil {
			return nil
		}
		imports = append(imports, imp)
	}

	var definitions []*Definition
	for p.current.Kind == TokenDef {
		definition := p.parseDefinition()
		if definition == nil {
			return nil
		}
		definitions = append(definitions, definition)
	}

	var body Node
	if module {
		if p.current.Kind != TokenEof {
			p.fail(ModuleHasBody, p.current)
			return nil
		}
	} else {
		if p.current.Kind == TokenEof {
			body = &Identity{base(NodeIdentity, pos(p.current))}
		} else {
			body = p.parsePipe()
		}
		if body == nil {
			return nil
		}
		if p.current.Kind != TokenEof {
			p.fail(SyntaxMessages.Unexpected(p.current), p.current)
			return nil
		}
	}

	return &Program{metadata, imports, definitions, body}
}

// parseImport: Import = 'import' STRING 'as' (IDENT | VAR) ConstObject? ';' | 'include' STRING ConstObject? ';'
func (p *Parser) parseImport() *Import {
	keyword := p.current
	p.advance()
	if p.current.Kind == TokenStringStart {
		p.fail(ImportPathNotConstant, p.current)
		return nil
	}
	if p.current.Kind != TokenString {
		p.fail(SyntaxMessages.Expected("a module path string", p.current), p.current)
		return nil
	}
	imp := &Import{Path: p.current.Text, Position: pos(keyword)}
	p.advance()

	if keyword.Kind == TokenImport {
		if !p.expect(TokenAs, "'as'") {
			return nil
		}
		if (p.current.Kind != TokenIdent && p.current.Kind != TokenVariable) || isLocation(p.current) {
			p.fail(SyntaxMessages.Expected("a module name or a $name", p.current), p.current)
			return nil
		}
		imp.Alias = p.current.Text
		imp.HasAlias = true
		imp.IsData = p.current.Kind == TokenVariable
		p.advance()
	}

	if p.current.Kind == TokenLeftBrace {
		imp.Metadata = p.parseMetadata()
		if imp.Metadata == nil {
			return nil
		}
	}

	if !p.expect(TokenSemicolon, "';'") {
		return nil
	}
	return imp
}

// parseMetadata parses the metadata of 'module' and of imports: a constant object.
func (p *Parser) parseMetadata() *values.Value {
	start := p.current
	value := p.parseConstant(start)
	if value == nil {
		return nil
	}
	if value.Kind() == values.KindObject {
		return value
	}
	p.fail(MetadataNotObject, start)
	return nil
}

// parseConstant: Const = NUMBER | STRING | null | true | false | '[' (Const (',' Const)*)? ']' |
// '{' (CEntry (',' CEntry)* ','?)? '}'; CEntry = (IDENT | Keyword | STRING) ':' Const. Any other
// token: "Module metadata must be constant" at the metadata.
func (p *Parser) parseConstant(start *Token) *values.Value {
	switch p.current.Kind {
	case TokenLeftBracket:
		return p.parseConstantArray(start)
	case TokenLeftBrace:
		return p.parseConstantObject(start)
	default:
		return p.parseConstantScalar(start)
	}
}

func (p *Parser) parseConstantScalar(start *Token) *values.Value {
	token := p.current
	p.advance()
	var value values.Value
	switch {
	case token.Kind == TokenNumber:
		value = values.FromNumber(token.Number)
	case token.Kind == TokenString:
		value = values.FromString(token.Text)
	case token.Kind == TokenIdent && token.Text == "null":
		value = values.Null
	case token.Kind == TokenIdent && (token.Text == "true" || token.Text == "false"):
		value = values.FromBool(token.Text == "true")
	default:
		p.fail(MetadataNotConstant, start)
		return nil
	}
	return &value
}

func (p *Parser) parseConstantArray(start *Token) *values.Value {
	if !p.enter(p.current) {
		return nil
	}
	p.advance()
	var items []values.Value
	for p.current.Kind != TokenRightBracket {
		if len(items) > 0 && !p.expectConstant(TokenComma, start) {
			return nil
		}
		item := p.parseConstant(start)
		if item == nil {
			return nil
		}
		items = append(items, *item)
	}

	p.advance()
	p.depth--
	value := values.FromArray(values.ArrayFromOwned(items))
	return &value
}

func (p *Parser) parseConstantObject(start *Token) *values.Value {
	if !p.enter(p.current) {
		return nil
	}
	p.advance()
	builder := values.NewObjectBuilder(4)
	for p.current.Kind != TokenRightBrace {
		key := p.current
		if key.Kind != TokenIdent && key.Kind != TokenString && !IsKeyword(key.Kind) {
			p.fail(MetadataNotConstant, start)
			return nil
		}
		p.advance()
		if !p.expectConstant(TokenColon, start) {
			return nil
		}
		value := p.parseConstant(start)
		if value == nil {
			return nil
		}
		builder.Set(key.Text, *value)
		if p.current.Kind == TokenComma {
			p.advance()
		} else if p.current.Kind != TokenRightBrace {
			p.fail(MetadataNotConstant, start)
			return nil
		}
	}

	p.advance()
	p.depth--
	value := values.FromObject(builder.ToObject())
	return &value
}

func (p *Parser) expectConstant(kind TokenKind, start *Token) bool {
	if p.current.Kind != kind {
		p.fail(MetadataNotConstant, start)
		return false
	}
	p.advance()
	return true
}

// parseDefinition: FuncDef = 'def' IDENT ('(' Param (';' Param)* ')')? ':' Pipe ';'
func (p *Parser) parseDefinition() *Definition {
	keyword := p.current
	if !p.enter(keyword) {
		return nil
	}
	p.advance()
	if p.current.Kind != TokenIdent {
		p.fail(SyntaxMessages.Expected("a function name", p.current), p.current)
		return nil
	}
	name := p.current.Text
	p.advance()
	parameters, ok := p.parseParams()
	if !ok || !p.expect(TokenColon, "':'") {
		return nil
	}
	body := p.parsePipe()
	if body == nil || !p.expect(TokenSemicolon, "';'") {
		return nil
	}
	p.depth--
	return &Definition{name, parameters, body, pos(keyword)}
}

// parseParams: ('(' Param (';' Param)* ')')?, Param = IDENT | VAR
func (p *Parser) parseParams() ([]*Param, bool) {
	var parameters []*Param
	if p.current.Kind != TokenLeftParen {
		return parameters, true
	}
	for {
		p.advance()
		if (p.current.Kind != TokenIdent && p.current.Kind != TokenVariable) || isLocation(p.current) {
			p.fail(SyntaxMessages.Expected("a parameter name", p.current), p.current)
			return nil, false
		}
		parameters = append(parameters, &Param{p.current.Text, p.current.Kind == TokenVariable, pos(p.current)})
		p.advance()
		if p.current.Kind != TokenSemicolon {
			break
		}
	}
	return parameters, p.expect(TokenRightParen, "')'")
}

// parsePipe: Pipe = Item (',' Item)* ('|' Pipe)?, where an Item is
//
//	'def' … ';' Pipe  |  'label' VAR '|' Pipe  |  Alt 'as' Patterns '|' Pipe  |  Alt
//
// The first three, and '|', take the rest of the pipe. To avoid recursing per element, each of
// them is kept as a partial node whose last child is a hole (nil), and the partial nodes are
// completed from the last to the first when the pipe ends.
func (p *Parser) parsePipe() Node {
	var pending []Node
	var items Node
	comma := p.current
	for {
		var partial Node
		if p.current.Kind == TokenDef {
			partial = p.parseFuncDef()
		} else if p.current.Kind == TokenLabel {
			partial = p.parseLabel()
		} else {
			item := p.parseExpression(false)
			if item == nil {
				return nil
			}
			if p.current.Kind == TokenAs {
				partial = p.parseBinding(item)
			} else {
				items = addItem(items, item, comma)
				if p.current.Kind == TokenComma {
					comma = p.current
					p.advance()
					continue
				}

				if p.current.Kind != TokenPipe {
					break
				}
				pending = append(pending, &Pipe{base(NodePipe, pos(p.current)), items, nil})
				p.advance()
				items = nil
				continue
			}
		}

		if partial == nil {
			return nil
		}

		// A binding takes the rest of the pipe; the items before it are the left side of a comma.
		if items == nil {
			pending = append(pending, partial)
		} else {
			pending = append(pending, addItem(items, partial, comma))
		}
		items = nil
	}

	last := items
	for i := len(pending) - 1; i >= 0; i-- {
		last = complete(pending[i], last)
	}
	return last
}

func addItem(items, item Node, comma *Token) Node {
	if items == nil {
		return item
	}
	return &Comma{base(NodeComma, pos(comma)), items, item}
}

// complete fills the hole of a partial node of parsePipe with rest.
func complete(partial, rest Node) Node {
	switch n := partial.(type) {
	case *Pipe:
		n.Right = rest
	case *Comma:
		n.Right = complete(n.Right, rest)
	case *FuncDef:
		n.Rest = rest
	case *Label:
		n.Body = rest
	case *Bind:
		n.Body = rest
	case *If:
		n.Else = rest
	default:
		return rest
	}
	return partial
}

// parseFuncDef parses 'def' … ';', as a partial node.
func (p *Parser) parseFuncDef() Node {
	definition := p.parseDefinition()
	if definition == nil {
		return nil
	}
	return &FuncDef{base(NodeFuncDef, definition.Position), definition, nil}
}

// parseLabel parses 'label' VAR '|', as a partial node.
func (p *Parser) parseLabel() Node {
	start := p.current
	p.advance()
	if p.current.Kind != TokenVariable {
		p.fail(SyntaxMessages.Expected("a label name ($name)", p.current), p.current)
		return nil
	}
	name := p.current.Text
	p.advance()
	if !p.expect(TokenPipe, "'|'") {
		return nil
	}
	return &Label{base(NodeLabel, pos(start)), name, nil}
}

// parseBinding parses Source 'as' Patterns '|', as a partial node.
func (p *Parser) parseBinding(source Node) Node {
	keyword := p.current
	p.advance()
	patterns := p.parsePatterns()
	if patterns == nil || !p.expect(TokenPipe, "'|'") {
		return nil
	}
	return &Bind{base(NodeBind, pos(keyword)), source, patterns, nil}
}

// parseObjectValue: ObjVal = Alt ('|' Alt)*: an object value, which has no top-level ',' and no
// 'as', 'def' or 'label' (as jq).
func (p *Parser) parseObjectValue() Node {
	var items []Node
	var bars []*Token
	for {
		item := p.parseExpression(false)
		if item == nil {
			return nil
		}
		items = append(items, item)
		if p.current.Kind != TokenPipe {
			break
		}
		bars = append(bars, p.current)
		p.advance()
	}

	result := items[len(items)-1]
	for i := len(items) - 2; i >= 0; i-- {
		result = &Pipe{base(NodePipe, pos(bars[i])), items[i], result}
	}
	return result
}

// parseExpression parses levels 4 to 13 of the precedence table: binary operators with an operand
// stack and an operator stack (shunting-yard), so a chain of any length does not recurse; each
// operand is Unary = '-'* Postfix, Postfix = Term Suffix*. With operandOnly, one operand (the
// Unary of 'try' and 'catch').
func (p *Parser) parseExpression(operandOnly bool) Node {
	var operands []Node
	var operators []*Token
	for {
		firstMinus := p.next
		for p.current.Kind == TokenMinus {
			p.advance()
		}
		termStart := p.next

		var operand Node
		switch p.current.Kind {
		case TokenLeftParen:
			operand = p.parseParenthesized()
		case TokenLeftBracket:
			operand = p.parseArray()
		case TokenLeftBrace:
			operand = p.parseObject()
		case TokenString, TokenStringStart:
			operand = p.parseString("", false)
		case TokenReduce, TokenForeach:
			operand = p.parseReduceOrForeach()
		case TokenIf:
			operand = p.parseIf()
		case TokenTry:
			operand = p.parseTry()
		case TokenIdent:
			if isLiteralName(p.current.Text) {
				operand = p.parseAtom()
			} else {
				operand = p.parseCall()
			}
		default:
			operand = p.parseAtom()
		}

		for operand != nil && p.isSuffix() {
			if p.isBracketSuffix() {
				operand = p.parseBracketSuffix(operand)
			} else {
				operand = p.parseSuffix(operand)
			}
		}
		if operand == nil {
			return nil
		}
		operand = p.negate(operand, firstMinus, termStart)
		if operandOnly {
			return operand
		}

		operands = append(operands, operand)
		if level, _ := binaryLevel(p.current.Kind); level == 0 {
			for len(operators) > 0 {
				operands, operators = reduceOperator(operands, operators)
			}
			return operands[0]
		}
		var ok bool
		if operands, operators, ok = p.pushOperator(operands, operators); !ok {
			return nil
		}
	}
}

func isLiteralName(name string) bool { return name == "null" || name == "true" || name == "false" }

// pushOperator reduces the operators that bind at least as tightly as the current one, then
// pushes it.
func (p *Parser) pushOperator(operands []Node, operators []*Token) ([]Node, []*Token, bool) {
	op := p.current
	level, associativity := binaryLevel(op.Kind)
	for len(operators) > 0 {
		top, _ := binaryLevel(operators[len(operators)-1].Kind)
		if top < level || (top == level && associativity == assocRight) {
			break
		}
		if top == level && associativity == assocNone {
			p.fail(SyntaxMessages.NotAssociative(op), op)
			return nil, nil, false
		}
		operands, operators = reduceOperator(operands, operators)
	}

	operators = append(operators, op)
	p.advance()
	return operands, operators, true
}

// reduceOperator replaces the last two operands with the last operator applied to them.
func reduceOperator(operands []Node, operators []*Token) ([]Node, []*Token) {
	op := operators[len(operators)-1]
	operators = operators[:len(operators)-1]
	right := operands[len(operands)-1]
	left := operands[len(operands)-2]
	operands = operands[:len(operands)-2]
	return append(operands, makeBinary(op, left, right)), operators
}

func makeBinary(op *Token, left, right Node) Node {
	at := pos(op)
	binary := func(o BinaryOp) Node { return &Binary{base(NodeBinary, at), o, left, right} }
	switch op.Kind {
	case TokenPlus:
		return binary(OpAdd)
	case TokenMinus:
		return binary(OpSubtract)
	case TokenStar:
		return binary(OpMultiply)
	case TokenSlash:
		return binary(OpDivide)
	case TokenPercent:
		return binary(OpModulo)
	case TokenEqual:
		return binary(OpEqual)
	case TokenNotEqual:
		return binary(OpNotEqual)
	case TokenLess:
		return binary(OpLess)
	case TokenLessEqual:
		return binary(OpLessEqual)
	case TokenGreater:
		return binary(OpGreater)
	case TokenGreaterEqual:
		return binary(OpGreaterEqual)
	case TokenAnd:
		return &And{base(NodeAnd, at), left, right}
	case TokenOr:
		return &Or{base(NodeOr, at), left, right}
	case TokenAlt:
		return &Alternative{base(NodeAlternative, at), left, right}
	case TokenAssign:
		return &Call{base(NodeCall, at), "_assign", []Node{left, right}}
	case TokenPipeAssign:
		return &Call{base(NodeCall, at), "_modify", []Node{left, right}}
	default:
		return makeUpdate(op.Kind, left, right, at)
	}
}

// makeUpdate rewrites L op= R to R as $v | _modify(L; . op $v).
func makeUpdate(kind TokenKind, left, right Node, at Position) Node {
	dot := &Identity{base(NodeIdentity, at)}
	value := &Variable{base(NodeVariable, at), UpdateVariable}
	var update Node
	binary := func(o BinaryOp) Node { return &Binary{base(NodeBinary, at), o, dot, value} }
	switch kind {
	case TokenPlusAssign:
		update = binary(OpAdd)
	case TokenMinusAssign:
		update = binary(OpSubtract)
	case TokenStarAssign:
		update = binary(OpMultiply)
	case TokenSlashAssign:
		update = binary(OpDivide)
	case TokenPercentAssign:
		update = binary(OpModulo)
	default:
		update = &Alternative{base(NodeAlternative, at), dot, value}
	}
	call := &Call{base(NodeCall, at), "_modify", []Node{left, update}}
	return &Bind{base(NodeBind, at), right, []Pattern{&VariablePattern{UpdateVariable, at}}, call}
}

// negate applies the minus tokens from first (included) to end (excluded) to node, the last one
// innermost.
func (p *Parser) negate(node Node, first, end int) Node {
	for i := end - 1; i >= first; i-- {
		node = &Negate{base(NodeNegate, pos(p.tokens[i])), node}
	}
	return node
}

func (p *Parser) isBracketSuffix() bool {
	return p.current.Kind == TokenLeftBracket || (p.current.Kind == TokenDot && p.peek().Kind == TokenLeftBracket)
}

func (p *Parser) isSuffix() bool {
	switch p.current.Kind {
	case TokenQuestion, TokenField, TokenLeftBracket, TokenQuestionAlt:
		return true
	case TokenDot:
		next := p.peek().Kind
		return next == TokenLeftBracket || next == TokenString || next == TokenStringStart
	}
	return false
}

// parseSuffix: Suffix = '?' | FIELD | '.' String; the bracket suffixes are parseBracketSuffix.
// '?//' is an error here: it separates patterns only.
func (p *Parser) parseSuffix(target Node) Node {
	token := p.current
	switch token.Kind {
	case TokenQuestion:
		p.advance()
		return &Try{base(NodeTry, pos(token)), target, nil}
	case TokenField:
		p.advance()
		key := &Literal{base(NodeLiteral, pos(token)), values.FromString(token.Text)}
		return &Index{base(NodeIndex, pos(token)), target, key}
	case TokenQuestionAlt:
		p.fail(SyntaxMessages.QuestionAlt(token), token)
		return nil
	default:
		p.advance()
		key := p.parseString("", false)
		if key == nil {
			return nil
		}
		return &Index{base(NodeIndex, pos(token)), target, key}
	}
}

// parseBracketSuffix: '.'? '[' ']' | '[' Pipe ']' | '[' Pipe ':' Pipe? ']' | '[' ':' Pipe ']'
func (p *Parser) parseBracketSuffix(target Node) Node {
	if p.current.Kind == TokenDot {
		p.advance()
	}
	open := p.current
	if !p.enter(open) {
		return nil
	}
	p.advance()
	var result Node
	if p.current.Kind == TokenRightBracket {
		result = &Iterate{base(NodeIterate, pos(open)), target}
	} else {
		var from Node
		if p.current.Kind != TokenColon {
			from = p.parsePipe()
			if from == nil {
				return nil
			}
		}

		if p.current.Kind == TokenColon {
			p.advance()
			var to Node
			if from == nil || p.current.Kind != TokenRightBracket {
				to = p.parsePipe()
				if to == nil {
					return nil
				}
			}
			result = &Slice{base(NodeSlice, pos(open)), target, from, to}
		} else {
			result = &Index{base(NodeIndex, pos(open)), target, from}
		}
	}

	return p.close(TokenRightBracket, "']'", result)
}

// parseParenthesized: '(' Pipe ')'
func (p *Parser) parseParenthesized() Node {
	if !p.enter(p.current) {
		return nil
	}
	p.advance()
	inner := p.parsePipe()
	if inner == nil {
		return nil
	}
	return p.close(TokenRightParen, "')'", inner)
}

// parseArray: '[' Pipe? ']'
func (p *Parser) parseArray() Node {
	open := p.current
	if !p.enter(open) {
		return nil
	}
	p.advance()
	if p.current.Kind == TokenRightBracket {
		return p.close(TokenRightBracket, "']'", &ArrayExpr{base(NodeArrayExpr, pos(open)), nil})
	}
	body := p.parsePipe()
	if body == nil {
		return nil
	}
	return p.close(TokenRightBracket, "']'", &ArrayExpr{base(NodeArrayExpr, pos(open)), body})
}

// parseAtom: '.', '..', '.' String, NUMBER, FORMAT String?, FIELD, VAR, null, true, false, 'break' VAR.
func (p *Parser) parseAtom() Node {
	token := p.current
	at := pos(token)
	switch token.Kind {
	case TokenDot:
		p.advance()
		if p.current.Kind == TokenString || p.current.Kind == TokenStringStart {
			key := p.parseString("", false)
			if key == nil {
				return nil
			}
			return &Index{base(NodeIndex, at), &Identity{base(NodeIdentity, at)}, key}
		}
		return &Identity{base(NodeIdentity, at)}
	case TokenDotDot:
		p.advance()
		return &Call{base(NodeCall, at), "recurse", nil}
	case TokenNumber:
		p.advance()
		return &Literal{base(NodeLiteral, at), values.FromNumber(token.Number)}
	case TokenFormat:
		// Any name parses: an unknown format is a runtime error (spec §9.9).
		p.advance()
		if p.current.Kind == TokenString || p.current.Kind == TokenStringStart {
			return p.parseString(token.Text, true)
		}
		return &Format{base(NodeFormat, at), token.Text}
	case TokenField:
		p.advance()
		key := &Literal{base(NodeLiteral, at), values.FromString(token.Text)}
		return &Index{base(NodeIndex, at), &Identity{base(NodeIdentity, at)}, key}
	case TokenVariable:
		p.advance()
		return &Variable{base(NodeVariable, at), token.Text}
	case TokenIdent:
		p.advance()
		value := values.FromBool(token.Text == "true")
		if token.Text == "null" {
			value = values.Null
		}
		return &Literal{base(NodeLiteral, at), value}
	case TokenBreak:
		p.advance()
		if p.current.Kind != TokenVariable {
			p.fail(SyntaxMessages.Expected("a label name ($name)", p.current), p.current)
			return nil
		}
		label := p.current.Text
		p.advance()
		return &Break{base(NodeBreak, at), label}
	default:
		p.fail(SyntaxMessages.Unexpected(token), token)
		return nil
	}
}

// parseCall: IDENT ('(' Pipe (';' Pipe)* ')')?
func (p *Parser) parseCall() Node {
	name := p.current
	p.advance()
	var arguments []Node
	if p.current.Kind == TokenLeftParen {
		if !p.enter(p.current) {
			return nil
		}
		for {
			p.advance()
			argument := p.parsePipe()
			if argument == nil {
				return nil
			}
			arguments = append(arguments, argument)
			if p.current.Kind != TokenSemicolon {
				break
			}
		}

		if !p.expect(TokenRightParen, "')'") {
			return nil
		}
		p.depth--
	}

	return &Call{base(NodeCall, pos(name)), name.Text, arguments}
}

// parseString parses a STRING, or StringStart Pipe (StringMiddle Pipe)* StringEnd.
func (p *Parser) parseString(format string, hasFormat bool) Node {
	start := p.current
	if start.Kind == TokenString {
		p.advance()
		return &Literal{base(NodeLiteral, pos(start)), values.FromString(start.Text)}
	}
	if !p.enter(start) {
		return nil
	}
	var texts []string
	var parts []Node
	for {
		texts = append(texts, p.current.Text)
		p.advance()
		part := p.parsePipe()
		if part == nil {
			return nil
		}
		parts = append(parts, part)
		if p.current.Kind != TokenStringMiddle && p.current.Kind != TokenStringEnd {
			p.fail(SyntaxMessages.Expected("')'", p.current), p.current)
			return nil
		}
		if p.current.Kind != TokenStringMiddle {
			break
		}
	}

	texts = append(texts, p.current.Text)
	p.advance()
	p.depth--
	return &Template{base(NodeTemplate, pos(start)), format, hasFormat, texts, parts}
}

// parseObject: '{' (Entry (',' Entry)* ','?)? '}'
func (p *Parser) parseObject() Node {
	open := p.current
	if !p.enter(open) {
		return nil
	}
	p.advance()
	var entries []*ObjectEntry
	for p.current.Kind != TokenRightBrace {
		entry := p.parseObjectKey()
		if entry != nil && entry.Value == nil && p.current.Kind == TokenColon {
			p.advance()
			value := p.parseObjectValue()
			if value == nil {
				entry = nil
			} else {
				entry.Value = value
			}
		}

		if entry == nil {
			return nil
		}
		entries = append(entries, entry)
		if p.current.Kind == TokenComma {
			p.advance()
		} else if p.current.Kind != TokenRightBrace {
			p.fail(SyntaxMessages.Expected("',' or '}'", p.current), p.current)
			return nil
		}
	}

	p.advance()
	p.depth--
	return &ObjectExpr{base(NodeObjectExpr, pos(open)), entries}
}

// parseObjectKey: Entry = (IDENT | Keyword | VAR | String | '(' Pipe ')') ':' ObjVal | IDENT |
// Keyword | VAR | String. The key of an entry, with a nil value when a ':' and the value follow. A
// shorthand is complete: {$x} has its value, {name} and {"s"} have none and no ':' after them.
func (p *Parser) parseObjectKey() *ObjectEntry {
	token := p.current
	at := pos(token)
	if token.Kind == TokenVariable {
		p.advance()
		if p.current.Kind == TokenColon {
			return &ObjectEntry{&Variable{base(NodeVariable, at), token.Text}, nil, at}
		}
		key := &Literal{base(NodeLiteral, at), values.FromString(token.Text)}
		return &ObjectEntry{key, &Variable{base(NodeVariable, at), token.Text}, at}
	}

	var key Node
	if token.Kind == TokenLeftParen {
		key = p.parseParenthesizedKey()
	} else {
		key = p.parseNameKey()
	}
	if key == nil {
		return nil
	}
	if token.Kind == TokenLeftParen && p.current.Kind != TokenColon {
		p.fail(SyntaxMessages.Expected("':'", p.current), p.current)
		return nil
	}
	return &ObjectEntry{key, nil, at}
}

// parseNameKey parses a key written as a name or a string (objects and object patterns):
// IDENT | Keyword | FORMAT? String.
func (p *Parser) parseNameKey() Node {
	token := p.current
	if token.Kind == TokenIdent || IsKeyword(token.Kind) {
		p.advance()
		return &Literal{base(NodeLiteral, pos(token)), values.FromString(token.Text)}
	}

	if token.Kind == TokenString || token.Kind == TokenStringStart {
		return p.parseString("", false)
	}

	if next := p.peek().Kind; token.Kind == TokenFormat && (next == TokenString || next == TokenStringStart) {
		p.advance()
		return p.parseString(token.Text, true)
	}

	switch token.Kind {
	case TokenComma, TokenRightBrace, TokenRightBracket, TokenRightParen, TokenSemicolon, TokenColon,
		TokenPipe, TokenEof, TokenError:
		p.fail(SyntaxMessages.Unexpected(token), token)
	default:
		p.fail(MayNeedParentheses, token)
	}
	return nil
}

// parseParenthesizedKey parses '(' Pipe ')' as a key; a number, boolean or null literal there is
// a static error (spec §3.2).
func (p *Parser) parseParenthesizedKey() Node {
	open := p.current
	if !p.enter(open) {
		return nil
	}
	p.advance()
	key := p.parsePipe()
	if key == nil || !p.expect(TokenRightParen, "')'") {
		return nil
	}
	p.depth--
	if literal, ok := key.(*Literal); ok && literal.Value.Kind() != values.KindString {
		p.failAt(SyntaxMessages.InvalidObjectKey(literal.Value), literal.Position)
		return nil
	}
	return key
}

// parseReduceOrForeach: 'reduce' Alt 'as' Patterns '(' Pipe ';' Pipe ')' | 'foreach' Alt 'as'
// Patterns '(' Pipe ';' Pipe (';' Pipe)? ')'
func (p *Parser) parseReduceOrForeach() Node {
	if !p.enter(p.current) {
		return nil
	}
	keyword := p.current
	p.advance()
	source := p.parseExpression(false)
	if source == nil || !p.expect(TokenAs, "'as'") {
		return nil
	}
	patterns := p.parsePatterns()
	if patterns == nil || !p.expect(TokenLeftParen, "'('") {
		return nil
	}

	limit := 3
	if keyword.Kind == TokenReduce {
		limit = 2
	}
	var bodies []Node
	for {
		if len(bodies) > 0 {
			p.advance()
		}
		body := p.parsePipe()
		if body == nil {
			return nil
		}
		bodies = append(bodies, body)
		if p.current.Kind != TokenSemicolon || len(bodies) >= limit {
			break
		}
	}

	if len(bodies) < 2 {
		p.fail(SyntaxMessages.Expected("';'", p.current), p.current)
		return nil
	}
	at := pos(keyword)
	if keyword.Kind == TokenReduce {
		return p.close(TokenRightParen, "')'", &Reduce{base(NodeReduce, at), source, patterns, bodies[0], bodies[1]})
	}
	var extract Node = &Identity{base(NodeIdentity, at)}
	if len(bodies) > 2 {
		extract = bodies[2]
	}
	return p.close(TokenRightParen, "')'", &Foreach{base(NodeForeach, at), source, patterns, bodies[0], bodies[1], extract})
}

// parseIf: 'if' Pipe 'then' Pipe ('elif' Pipe 'then' Pipe)* ('else' Pipe)? 'end'. Each branch is a
// partial If whose else is a hole, completed from the last to the first.
func (p *Parser) parseIf() Node {
	if !p.enter(p.current) {
		return nil
	}
	var branches []Node
	for {
		keyword := p.current
		p.advance()
		condition := p.parsePipe()
		if condition == nil || !p.expect(TokenThen, "'then'") {
			return nil
		}
		then := p.parsePipe()
		if then == nil {
			return nil
		}
		branches = append(branches, &If{base(NodeIf, pos(keyword)), condition, then, nil})
		if p.current.Kind != TokenElif {
			break
		}
	}

	var otherwise Node
	if p.current.Kind == TokenElse {
		p.advance()
		otherwise = p.parsePipe()
		if otherwise == nil {
			return nil
		}
	} else {
		otherwise = &Identity{base(NodeIdentity, pos(p.current))}
	}

	for i := len(branches) - 1; i >= 0; i-- {
		otherwise = complete(branches[i], otherwise)
	}
	return p.close(TokenEnd, "'end'", otherwise)
}

// parseTry: 'try' Unary ('catch' Unary)?
func (p *Parser) parseTry() Node {
	keyword := p.current
	if !p.enter(keyword) {
		return nil
	}
	p.advance()
	body := p.parseExpression(true)
	if body == nil {
		return nil
	}
	var handler Node
	if p.current.Kind == TokenCatch {
		p.advance()
		handler = p.parseExpression(true)
		if handler == nil {
			return nil
		}
	}

	p.depth--
	return &Try{base(NodeTry, pos(keyword)), body, handler}
}

// parsePatterns: Patterns = Pattern ('?//' Pattern)*
func (p *Parser) parsePatterns() []Pattern {
	var patterns []Pattern
	for {
		pattern := p.parsePattern()
		if pattern == nil {
			return nil
		}
		patterns = append(patterns, pattern)
		if p.current.Kind != TokenQuestionAlt {
			return patterns
		}
		p.advance()
	}
}

// parsePattern: Pattern = VAR | '[' Pattern (',' Pattern)* ']' | '{' PEntry (',' PEntry)* '}'
func (p *Parser) parsePattern() Pattern {
	token := p.current
	at := pos(token)
	switch {
	case token.Kind == TokenVariable && !isLocation(token):
		p.advance()
		return &VariablePattern{token.Text, at}
	case token.Kind == TokenLeftBracket:
		if !p.enter(token) {
			return nil
		}
		var elements []Pattern
		for {
			p.advance()
			element := p.parsePattern()
			if element == nil {
				return nil
			}
			elements = append(elements, element)
			if p.current.Kind != TokenComma {
				break
			}
		}

		if !p.expect(TokenRightBracket, "']'") {
			return nil
		}
		p.depth--
		return &ArrayPattern{elements, at}
	case token.Kind == TokenLeftBrace:
		if !p.enter(token) {
			return nil
		}
		var entries []*PatternEntry
		for {
			p.advance()
			entry := p.parsePatternEntry()
			if entry == nil {
				return nil
			}
			entries = append(entries, entry)
			if p.current.Kind != TokenComma {
				break
			}
		}

		if !p.expect(TokenRightBrace, "'}'") {
			return nil
		}
		p.depth--
		return &ObjectPattern{entries, at}
	default:
		p.fail(SyntaxMessages.Expected("a pattern ($name, [...] or {...})", token), token)
		return nil
	}
}

// parsePatternEntry: PEntry = VAR (':' Pattern)? | (IDENT | Keyword | String | '(' Pipe ')') ':' Pattern
func (p *Parser) parsePatternEntry() *PatternEntry {
	token := p.current
	at := pos(token)
	if isLocation(token) {
		p.fail(SyntaxMessages.Unexpected(token), token)
		return nil
	}
	entry := &PatternEntry{Position: at}
	if token.Kind == TokenVariable {
		p.advance()
		entry.Key = &Literal{base(NodeLiteral, at), values.FromString(token.Text)}
		entry.Variable = token.Text
		entry.HasVariable = true
		if p.current.Kind != TokenColon {
			return entry
		}
	} else {
		if token.Kind == TokenLeftParen {
			entry.Key = p.parseParenthesizedKey()
		} else {
			entry.Key = p.parseNameKey()
		}
		if entry.Key == nil {
			return nil
		}
	}

	if !p.expect(TokenColon, "':'") {
		return nil
	}
	entry.Value = p.parsePattern()
	if entry.Value == nil {
		return nil
	}
	return entry
}

// enter opens one level of nesting (spec §11).
func (p *Parser) enter(token *Token) bool {
	p.depth++
	if p.depth <= MaxNesting {
		return true
	}
	p.fail(TooDeeplyNested, token)
	return false
}

// close expects the token that closes a nesting level, and leaves the level.
func (p *Parser) close(kind TokenKind, what string, node Node) Node {
	if !p.expect(kind, what) {
		return nil
	}
	p.depth--
	return node
}

func (p *Parser) expect(kind TokenKind, what string) bool {
	if p.current.Kind != kind {
		p.fail(SyntaxMessages.Expected(what, p.current), p.current)
		return false
	}
	p.advance()
	return true
}

func (p *Parser) failAt(message string, position Position) {
	if p.err == nil {
		p.err = &SyntaxError{Message: message, Line: position.Line, Column: position.Column}
	}
}

// fail records the error (the first one wins). At a lexical error token, the lexer's message wins.
func (p *Parser) fail(message string, token *Token) {
	if p.err != nil {
		return
	}
	if token.Kind == TokenError {
		message = token.Text
	}
	p.err = &SyntaxError{Message: message, Line: token.Line, Column: token.Column}
}
