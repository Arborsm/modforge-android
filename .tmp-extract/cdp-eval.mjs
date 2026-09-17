// Minimal CDP Runtime.evaluate client: node cdp-eval.mjs "<expression>" [--url <substring>]
// Usage example: node cdp-eval.mjs "document.elementFromPoint(421,60).className"
import { readFileSync } from 'node:fs'

const port = process.env.CDP_PORT ?? '9333'
const args = process.argv.slice(2)
const urlFilter = args.includes('--url') ? args[args.indexOf('--url') + 1] : null
const expression = args.filter((arg, index) => !(arg === '--url' || args[index - 1] === '--url')).join(' ')

const pages = await (await fetch(`http://127.0.0.1:${port}/json`)).json()
const page = pages.find((candidate) => (urlFilter ? candidate.url.includes(urlFilter) : true))
  ?? pages[0]
if (!page) {
  console.error('no debuggable page')
  process.exit(1)
}

const ws = new WebSocket(page.webSocketDebuggerUrl)
await new Promise((resolve, reject) => {
  ws.onopen = resolve
  ws.onerror = reject
})

const result = await new Promise((resolve) => {
  ws.onmessage = (event) => {
    const message = JSON.parse(event.data)
    if (message.id === 1) resolve(message)
  }
  ws.send(JSON.stringify({
    id: 1,
    method: 'Runtime.evaluate',
    params: {
      expression,
      returnByValue: true,
      awaitPromise: true,
      userGesture: true,
    },
  }))
})

if (result.result?.exceptionDetails) {
  console.error('EXCEPTION:', JSON.stringify(result.result.exceptionDetails, null, 2))
} else {
  console.log(JSON.stringify(result.result?.result?.value, null, 2))
}
ws.close()
process.exit(0)
