import { readFileSync, writeFileSync, readdirSync, statSync, existsSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const packages = [
  { id: 'PulseSharp.Abstractions', page: 'abstractions', intro: 'Contracts for handlers and libraries. No dependencies.' },
  { id: 'PulseSharp', page: 'core', intro: 'The engine: setup, the in-memory store, and the control API.' }
]

const builtInNames = {
  String: 'string', Int32: 'int', Int64: 'long', Boolean: 'bool', Object: 'object', Void: 'void'
}

function sourceFiles(directory) {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry)
    if (statSync(path).isDirectory()) {
      return entry === 'bin' || entry === 'obj' ? [] : sourceFiles(path)
    }
    return path.endsWith('.cs') ? [path] : []
  })
}

function readDeclarations(packageId) {
  const declarations = new Map()
  const pattern = /^public\s+(?:(?:sealed|abstract|static|readonly|partial)\s+)*(?:record struct|record|class|interface|enum|struct)\s+(\w+)/gm
  for (const file of sourceFiles(join(root, 'src', packageId))) {
    for (const match of readFileSync(file, 'utf8').matchAll(pattern)) {
      declarations.set(match[1], match[0])
    }
  }
  return declarations
}

function shortTypeName(fullName, methodTypeParameters = []) {
  let text = fullName
    .replace(/``(\d+)/g, (_, index) => methodTypeParameters[Number(index)] ?? `T${index}`)
    .replace(/`(\d+)/g, '')
  text = text.replace(/System\.Nullable\{([^{}]+)\}/g, (_, inner) => `${inner}?`)
  text = text.replace(/[\w.]+/g, (path) => {
    const last = path.split('.').pop()
    return builtInNames[last] ?? last
  })
  return text.replace(/\{/g, '<').replace(/\}/g, '>').replace(/,/g, ', ')
}

function splitParameters(list) {
  const parts = []
  let depth = 0
  let current = ''
  for (const character of list) {
    if (character === '{') depth++
    if (character === '}') depth--
    if (character === ',' && depth === 0) {
      parts.push(current)
      current = ''
    } else {
      current += character
    }
  }
  if (current) parts.push(current)
  return parts
}

function tagValues(body, tag) {
  return [...body.matchAll(new RegExp(`<${tag} name="([^"]+)">([\\s\\S]*?)</${tag}>`, 'g'))].map(([, name, text]) => ({ name, text }))
}

function tagText(body, tag) {
  return body.match(new RegExp(`<${tag}>([\\s\\S]*?)</${tag}>`))?.[1]
}

function parseMembers(xml) {
  return [...xml.matchAll(/<member name="([^"]+)">([\s\S]*?)<\/member>/g)].map(([, id, body]) => {
    const kind = id[0]
    const rest = id.slice(2)
    const withoutParameters = rest.replace(/\(.*\)$/, '')
    const parameterList = rest.match(/\((.*)\)$/)?.[1]
    if (kind === 'T') {
      return { kind, id, body, typeFullName: rest, name: rest.split('.').pop() }
    }
    const lastDot = withoutParameters.lastIndexOf('.')
    return {
      kind,
      id,
      body,
      typeFullName: withoutParameters.slice(0, lastDot),
      name: withoutParameters.slice(lastDot + 1),
      parameterList
    }
  })
}

function createRenderer(typePages) {
  function typeLink(fullName) {
    const name = shortTypeName(fullName)
    const page = typePages.get(fullName)
    return page ? `[\`${name}\`](/reference/${page}#${name.toLowerCase()})` : `\`${name}\``
  }

  function crefText(cref) {
    const kind = cref[0]
    const target = cref.slice(2).replace(/\(.*\)$/, '')
    if (kind === 'T') return typeLink(target)
    const parts = target.split('.')
    const member = parts.pop().replace(/``\d+$/, '').replace('#ctor', 'constructor')
    return `\`${shortTypeName(parts.pop())}.${member}\``
  }

  return function render(fragment = '') {
    const codeBlocks = []
    let text = fragment.replace(/<code>([\s\S]*?)<\/code>/g, (_, code) => {
      const lines = code.replace(/^\n/, '').replace(/\s+$/, '').split('\n')
      const indent = Math.min(...lines.filter((line) => line.trim()).map((line) => line.match(/^ */)[0].length))
      const decoded = lines.map((line) => line.slice(indent)).join('\n').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&')
      codeBlocks.push('\n```csharp\n' + decoded + '\n```\n')
      return `@@CODE${codeBlocks.length - 1}@@`
    })
    text = text
      .replace(/<see cref="([^"]+)"\s*\/>/g, (_, cref) => crefText(cref))
      .replace(/<see langword="([^"]+)"\s*\/>/g, '`$1`')
      .replace(/<(?:paramref|typeparamref) name="([^"]+)"\s*\/>/g, '`$1`')
      .replace(/<c>([\s\S]*?)<\/c>/g, '`$1`')
      .replace(/<para>/g, '\n\n')
      .replace(/<\/para>/g, '\n\n')
      .replace(/<[^>]+>/g, '')
      .replace(/&lt;/g, '<')
      .replace(/&gt;/g, '>')
      .replace(/&amp;/g, '&')
      .split(/\n\s*\n/)
      .map((paragraph) => paragraph.replace(/\s+/g, ' ').trim())
      .filter(Boolean)
      .join('\n\n')
    return text.replace(/@@CODE(\d+)@@/g, (_, index) => codeBlocks[Number(index)])
  }
}

function memberTitle(member, typeName) {
  const typeParameters = tagValues(member.body, 'typeparam').map((value) => value.name)
  if (member.kind === 'P' || member.kind === 'F') return member.name
  const genericArity = Number(member.name.match(/``(\d+)$/)?.[1] ?? 0)
  const methodName = member.name === '#ctor' ? typeName : member.name.replace(/``\d+$/, '')
  const generics = genericArity ? `<${typeParameters.slice(0, genericArity).join(', ')}>` : ''
  const parameters = member.parameterList
    ? splitParameters(member.parameterList).map((parameter) => shortTypeName(parameter, typeParameters)).join(', ')
    : ''
  return `${methodName}${generics}(${parameters})`
}

function table(rows, header) {
  if (rows.length === 0) return ''
  return `| ${header[0]} | ${header[1]} |\n|---|---|\n` + rows.map(([name, text]) => `| \`${name}\` | ${text.replace(/\|/g, '\\|').replace(/\n+/g, ' ')} |`).join('\n') + '\n\n'
}

function renderMemberDetails(body, render) {
  let output = ''
  const summary = render(tagText(body, 'summary'))
  if (summary) output += `${summary}\n\n`
  output += table(tagValues(body, 'typeparam').map((value) => [value.name, render(value.text)]), ['Type parameter', 'Description'])
  output += table(tagValues(body, 'param').map((value) => [value.name, render(value.text)]), ['Parameter', 'Description'])
  const returns = render(tagText(body, 'returns'))
  if (returns) output += `**Returns:** ${returns}\n\n`
  const exceptions = [...body.matchAll(/<exception cref="([^"]+)">([\s\S]*?)<\/exception>/g)]
  for (const [, cref, text] of exceptions) {
    output += `**Throws** \`${shortTypeName(cref.slice(2))}\` when ${render(text).replace(/^./, (c) => c.toLowerCase())}\n\n`
  }
  const remarks = render(tagText(body, 'remarks'))
  if (remarks) output += `${remarks}\n\n`
  const example = render(tagText(body, 'example'))
  if (example) output += `**Example**\n\n${example}\n\n`
  return output
}

const parsed = packages.map((pkg) => {
  const path = join(root, 'src', pkg.id, 'bin', 'Release', 'net8.0', `${pkg.id}.xml`)
  if (!existsSync(path)) {
    throw new Error(`Missing ${path}. Run "dotnet build -c Release" first.`)
  }
  return { ...pkg, members: parseMembers(readFileSync(path, 'utf8')), declarations: readDeclarations(pkg.id) }
})

const typePages = new Map()
for (const pkg of parsed) {
  for (const member of pkg.members.filter((m) => m.kind === 'T')) {
    typePages.set(member.typeFullName, pkg.page)
  }
}

const render = createRenderer(typePages)

for (const pkg of parsed) {
  const types = pkg.members
    .filter((member) => member.kind === 'T')
    .sort((left, right) => left.name.localeCompare(right.name))

  let page = `---\noutline: 2\n---\n\n# ${pkg.id}\n\n${pkg.intro}\n\n`
  page += `> Generated from the XML documentation of \`${pkg.id}\`.\n\n`

  for (const type of types) {
    const typeName = type.name.replace(/`\d+$/, '')
    page += `## ${typeName}\n\n`
    const declaration = pkg.declarations.get(typeName)
    if (declaration) page += '```csharp\n' + declaration + '\n```\n\n'
    page += renderMemberDetails(type.body, render)

    const members = pkg.members.filter((member) => member.kind !== 'T' && member.typeFullName === type.typeFullName)
    for (const member of members) {
      page += `### \`${memberTitle(member, typeName)}\`\n\n`
      page += renderMemberDetails(member.body, render)
    }
  }

  writeFileSync(join(root, 'docs', 'reference', `${pkg.page}.md`), page)
  console.log(`docs/reference/${pkg.page}.md: ${types.length} types`)
}
