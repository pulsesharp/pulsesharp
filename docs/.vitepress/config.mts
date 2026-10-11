import { defineConfig } from 'vitepress'

const base = '/pulsesharp/'

export default defineConfig({
  title: 'PulseSharp',
  description: 'Lightweight events and jobs for .NET — from the device to the cloud.',
  base,
  cleanUrls: true,
  srcExclude: ['scripts/**', 'README.md'],
  head: [
    ['link', { rel: 'icon', type: 'image/svg+xml', href: `${base}favicon.svg` }],
    ['meta', { name: 'theme-color', content: '#0A0F1A' }],
    ['meta', { property: 'og:title', content: 'PulseSharp' }],
    ['meta', { property: 'og:description', content: 'Lightweight events and jobs for .NET — from the device to the cloud.' }],
    ['meta', { property: 'og:image', content: `https://pulsesharp.github.io${base}social-preview.png` }],
    ['link', { rel: 'preconnect', href: 'https://fonts.googleapis.com' }],
    ['link', { rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: '' }],
    ['link', {
      rel: 'stylesheet',
      href: 'https://fonts.googleapis.com/css2?family=IBM+Plex+Sans:wght@400;500;600&family=JetBrains+Mono:wght@400;600&family=Space+Grotesk:wght@500;700&display=swap'
    }]
  ],
  themeConfig: {
    logo: { light: '/mark-on-light.svg', dark: '/mark-on-dark.svg', alt: 'PulseSharp' },
    nav: [
      { text: 'Guide', link: '/guide/getting-started', activeMatch: '/guide/' },
      { text: 'Concepts', link: '/concepts/delivery', activeMatch: '/concepts/' },
      { text: 'API', link: '/reference/', activeMatch: '/reference/' },
      { text: 'Changelog', link: '/changelog' },
      { text: 'NuGet', link: 'https://www.nuget.org/packages/PulseSharp' }
    ],
    sidebar: {
      '/guide/': [
        {
          text: 'Start',
          items: [
            { text: 'Introduction', link: '/guide/introduction' },
            { text: 'Getting started', link: '/guide/getting-started' }
          ]
        },
        {
          text: 'Events',
          items: [
            { text: 'Emitting events', link: '/guide/events' },
            { text: 'Handlers', link: '/guide/handlers' },
            { text: 'Failures and control', link: '/guide/failures' }
          ]
        },
        {
          text: 'Jobs',
          items: [{ text: 'Background jobs', link: '/guide/jobs' }]
        },
        {
          text: 'Setup',
          items: [
            { text: 'Stores (SQLite)', link: '/guide/stores' },
            { text: 'Configuration', link: '/guide/configuration' },
            { text: 'Desktop, web and worker apps', link: '/guide/hosting' },
            { text: 'Trimming and Native AOT', link: '/guide/native-aot' }
          ]
        }
      ],
      '/concepts/': [
        {
          text: 'Concepts',
          items: [
            { text: 'Delivery guarantees', link: '/concepts/delivery' },
            { text: 'Architecture', link: '/concepts/architecture' },
            { text: 'Vision and roadmap', link: '/concepts/vision' }
          ]
        }
      ],
      '/reference/': [
        {
          text: 'API reference',
          items: [
            { text: 'Overview', link: '/reference/' },
            { text: 'PulseSharp.Abstractions', link: '/reference/abstractions' },
            { text: 'PulseSharp', link: '/reference/core' },
            { text: 'PulseSharp.Sqlite', link: '/reference/sqlite' }
          ]
        }
      ]
    },
    socialLinks: [{ icon: 'github', link: 'https://github.com/pulsesharp/pulsesharp' }],
    search: { provider: 'local' },
    editLink: {
      pattern: 'https://github.com/pulsesharp/pulsesharp/edit/main/docs/:path',
      text: 'Edit this page on GitHub'
    },
    outline: { level: [2, 3] },
    footer: {
      message: 'Released under the MIT License.',
      copyright: 'Copyright © 2026 PulseSharp'
    }
  }
})
