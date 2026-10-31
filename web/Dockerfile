# Angular dev server for docker compose; production hosting is out of scope
# Keep the major in sync with web/.nvmrc
FROM node:26-slim
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
EXPOSE 4200
USER node
CMD ["npm", "start"]
