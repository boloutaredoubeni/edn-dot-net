﻿
namespace EDNReaderWriter
module EDNParser =
    open System.IO
    open System.Text.RegularExpressions
    open System.Numerics
    open FParsec
    open EDNReaderWriter.EDNParserTypes

    //trace function from fparsec docs
    let internal (<!>) (p: Parser<_,_>) label : Parser<_,_> =
        fun stream ->
            printfn "%A: Entering %s" stream.Position label
            let reply = p stream
            printfn "%A: Leaving %s (%A)" stream.Position label reply.Status
            reply

    let internal isWhiteSpace c = 
        match c with 
        ' ' |  '\r' | '\t' | '\n' | ',' -> true
        | _ -> false

    let internal whiteSpace : Parser<char, unit> = 
        satisfy isWhiteSpace

    let internal seperator : Parser<char, unit> =
        anyOf "()[]{} \r\t\n\\;'@^`~,\"%"

    let internal skipWhiteSpace = many whiteSpace |>> ignore 

    let internal parseNil : Parser<EDNValue, unit> = 
        attempt (skipWhiteSpace >>. (stringReturn "nil" EDNNil)
                 .>> (lookAhead eof <|> lookAhead (seperator |>> ignore)))

    let internal parseBool : Parser<EDNValue, unit> = 
        attempt (skipWhiteSpace >>. (choice [ pstring "true" >>% EDNBoolean(true) ; pstring "false" >>% EDNBoolean(false)])
                .>> (lookAhead eof <|> lookAhead (seperator |>> ignore)))
        
    let internal parseString : Parser<EDNValue, unit> =
        let normalChar = satisfy (fun c -> c <> '\\' && c <> '"')
        let unescape c = match c with
                         | 'n' -> '\n'
                         | 'r' -> '\r'
                         | 't' -> '\t'
                         |  c  -> c
        let escapedChar = pstring "\\" >>. (anyOf "\\nrt\"" |>> unescape)
        skipWhiteSpace >>.
        between (pstring "\"") (pstring "\"")
                (manyChars (normalChar <|> escapedChar)) |>> fun s -> EDNString(s)

    let internal parseCharacter : Parser<EDNValue, unit> = 
        attempt 
            skipWhiteSpace >>.
                pchar '\\' >>.
                (pstring "newline" <|> pstring "return" <|> pstring "space" <|> pstring "tab" <|> (noneOf [' ' ; '\r' ; '\t' ; '\n' ] 
                |>> (fun c -> string(c))) |>> fun s ->
                    match s with
                    | "newline" -> EDNCharacter('\n')
                    | "return" -> EDNCharacter('\r')
                    | "space" -> EDNCharacter(' ')
                    | "tab" -> EDNCharacter('\t')
                    | _ -> EDNCharacter(s.Chars 0))
             .>> (lookAhead eof <|> lookAhead (seperator |>> ignore))

    let internal numberFormat = NumberLiteralOptions.AllowMinusSign
                               ||| NumberLiteralOptions.AllowFraction
                               ||| NumberLiteralOptions.AllowExponent

    let internal parseNumber : Parser<EDNValue, unit> = 
        skipWhiteSpace >>.
        numberLiteral numberFormat "number"
        |>> fun nl ->
                if nl.IsInteger then EDNInteger(BigInteger.Parse nl.String)
                else EDNFloat(float nl.String)
    
    let internal parseComment = 
        skipWhiteSpace >>. 
        pchar ';' >>. 
        restOfLine false |>> (fun s -> EDNComment(s))

    let internal isAlphaChar = 
        let regex = new Regex("[a-zA-Z]")
        fun (c : char) ->
            regex.IsMatch(string(c))

    let internal isValidInnerSymbolChar = 
        let regex = new Regex("[a-zA-Z0-9#:.*!?$%&=+_-]")
        fun (c : char) ->
            regex.IsMatch(string(c))

    let internal isValidFirstSymbolChar = 
        let regex = new Regex("[a-zA-Z.*!?$%&=+_-]")
        fun (c : char) ->
            regex.IsMatch(string(c))

    let internal isNonNumericSymbolChar = 
        let regex = new Regex("[a-zA-Z#:.*!?$%&=+_-]")
        fun (c : char) ->
            regex.IsMatch(string(c))

    let internal parseSymbolPart : Parser<string, unit> = 
        skipWhiteSpace >>.
        satisfy isValidFirstSymbolChar <?> "Symbol has invalid first character" >>=
            fun firstChar ->
                match firstChar with
                | '+' | '-' | '.'  -> satisfy isNonNumericSymbolChar .>>. manySatisfy isValidInnerSymbolChar 
                                   |>> fun (nonNumeric, rest) -> System.String.Format("{0}{1}{2}", firstChar, nonNumeric, rest)
                | _ -> manySatisfy isValidInnerSymbolChar |>>  fun s -> System.String.Format("{0}{1}", firstChar, s)
     
    let internal parseQualifiedSymbol =
        attempt (parseSymbolPart .>> (pchar '/')) .>>. parseSymbolPart |>> 
            fun (prefix, name) -> EDNSymbol(QualifiedSymbol(prefix, name)) 
        
    let internal parseSymbol =
        skipWhiteSpace >>.
        parseQualifiedSymbol <|> (parseSymbolPart |>> fun s-> EDNSymbol(QualifiedSymbol(null, s)))
        
    let internal parseKeyWord = 
        skipWhiteSpace >>.
        pchar ':' >>.
        parseSymbol |>> function (EDNSymbol qualifiedSymbol) -> EDNKeyword(qualifiedSymbol) 
                                 | _ -> raise (System.Exception("Invalid keyword."))

    let internal parseTag =
         attempt (pchar '#' >>. lookAhead (satisfy isAlphaChar)) >>.
         parseSymbol |>> function (EDNSymbol qs) -> qs | _ -> raise (System.Exception("Invalid tag."))

    let rec parseValue : Parser<_,_> =
        fun stream ->
            let p = skipWhiteSpace |>> fun _ -> (stream.Line, stream.Column)
                    .>>.
                    (attempt parseComment
                             <|>  parseDiscard
                             <|>  parseTaggedValue 
                             <|>  parseSet <|> parseList
                             <|>  parseVector <|> parseMap
                             <|>  parseNil <|> parseBool
                             <|>  parseNumber
                             <|>  parseKeyWord <|> parseSymbol
                             <|>  parseCharacter
                             <|>  parseString) .>>
                    skipWhiteSpace 
                    |>> fun ((line, col), ednValue) -> new EDNValueParsed(ednValue, line, col)
            p stream   

    and internal parseDiscard = 
        skipWhiteSpace >>.
        pstring "#_" >>.
        parseValue |>> fun (value) -> EDNDiscard(value)

    and internal parseTaggedValue : Parser<EDNValue, unit> = 
        skipWhiteSpace >>.
        parseTag .>>.
        parseValue |>> fun (symbol, value) -> EDNTaggedValue(symbol, value)
     
    and internal parseList = 
        skipWhiteSpace >>.
        pchar '(' >>.
        many parseValue .>>
        pchar ')' |>> fun l -> EDNList(l)
    
    and internal parseSet = 
        skipWhiteSpace >>.
        pchar '#' >>.
        pchar '{' >>.
        many parseValue .>>
        pchar '}' |>> fun l -> EDNSet(l)
    
    and internal parseMap = 
        let parseEvenList l = 
            let filteredList = List.filter isNotCommentOrDiscard l
            if filteredList.Length % 2 = 0 then
                preturn l
            else
                fail "Map must have even number of elements"

        skipWhiteSpace >>.
        pchar '{' >>.
        many parseValue >>= parseEvenList .>>
        pchar '}' |>> fun l -> EDNMap(l)

    and internal parseVector = 
        skipWhiteSpace >>.
        pchar '[' >>.
        many parseValue .>>
        pchar ']'  |>> fun l -> EDNVector(Array.ofList l)

    let internal getValueFromResult result =
        match result with
        | Success (r,_,_) -> r
        | Failure (r,_,_) -> raise (System.Exception (r))


    type public EDNParserFuncs =
        static member parseString str = 
            run (many1 parseValue) str |> getValueFromResult 

        static member parseStream stream = 
            runParserOnStream parseValue () "ednStream" stream System.Text.Encoding.UTF8 |> getValueFromResult

        static member parseFile fileName = 
            runParserOnFile (many1 parseValue) () fileName System.Text.Encoding.UTF8 |> getValueFromResult 

        static member parseDirectory dir  = 
            let searchPattern = @"*.edn"
            let testFiles = Directory.GetFiles(dir, searchPattern, SearchOption.AllDirectories)
            let results = [for f in testFiles do yield! EDNParserFuncs.parseFile f]
            results
