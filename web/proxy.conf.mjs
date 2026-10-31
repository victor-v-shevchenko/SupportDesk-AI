// Dev server proxy: /api goes to the API. API_URL is set in docker-compose.yml, where the API is another container.
export default {
  '/api': {
    target: process.env.API_URL ?? 'http://localhost:5080',
    secure: false,
    changeOrigin: true,
  },
};
