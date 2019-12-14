# GitHub PR Statistics

Create statistics of users pull requests

#### Libraries

* Polly 
* FluentScheduler

#### GitHub API

##### Token

Create GitHub Personal Access Token. No scopes are required.

#### Rate limits

GitHub has request rate limits. Server will return `403 Forbidden` when all requests are used. Request has reset time in `X-RateLimit-Reset` header.

##### Endpoints

Pull requests from specific user
```
https://api.github.com/search/issues?q=author%3Attu+type%3Apr
```

Define page
```
https://api.github.com/search/issues?q=author%3Attu+type%3Apr&page=2
```

List users from finland

```
https://api.github.com/search/users?q=location:finland
```

List users with repo count greater than 5
```
https://api.github.com/search/users?q=location:finland+repos:%3E5
```